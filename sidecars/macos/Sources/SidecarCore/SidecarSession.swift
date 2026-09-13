import Foundation

/// What the menu bar shows, and what the app is actually doing.
public enum SidecarStatus: Equatable, Sendable {
    /// No credential stored. The operator needs to pair.
    case unpaired

    /// A pairing attempt is in flight.
    case pairing

    /// Paired and checking in successfully.
    case connected(workerId: Int, lastCheckIn: Date)

    /// Paired and running a job the server handed over.
    case working(jobId: Int, progress: JobProgress)

    /// Paired, but the last check-in did not get through. The credential is still believed good,
    /// so this recovers on its own — distinct from `revoked`, which never will.
    case unreachable(reason: String)

    /// The server refused the credential. Someone revoked this worker, or the database was
    /// replaced. Only re-pairing fixes it, so the stored credential is discarded.
    case revoked

    /// Remote workers are switched off on the server. Nothing is wrong with this app; it simply
    /// has nothing to do until an operator turns the feature on.
    case disabledOnServer(reason: String)

    /// Pairing failed for a reason the operator needs to read.
    case pairingFailed(reason: String)
}

/// Drives pairing and the check-in loop, and owns the one piece of state the UI renders.
///
/// Deliberately free of SwiftUI so the whole lifecycle — pair, beat, get revoked, recover — can be
/// tested without a menu bar. `@MainActor` because the UI observes it directly and there is no
/// reason for this to be concurrent; the work it does is network I/O, not computation.
@MainActor
public final class SidecarSession: ObservableObject {
    @Published public internal(set) var status: SidecarStatus = .unpaired
    @Published public internal(set) var serverAddress: String = ""

    /// How the last job ended, kept so the menu can say what this machine last did for the server.
    @Published public internal(set) var lastOutcome: JobOutcome?

    /// Every job in flight and where it has got to, keyed by job id.
    @Published public internal(set) var activeJobs: [Int: JobProgress] = [:]

    /// The recent frames each running job was seen encoding. Only collected while the menu is
    /// open, and dropped as soon as the job ends.
    @Published public internal(set) var filmStrips: [Int: FilmStrip] = [:]

    /// The GPU reading taken alongside the last preview, when this Mac publishes one.
    @Published public internal(set) var gpu: GpuUsage?

    /// How many jobs this Mac takes at once. Chosen by the operator, reported to the server on
    /// every check-in, and the ceiling the claim loop fills up to.
    @Published public private(set) var jobConcurrency: Int

    public static let concurrencyRange = 1...4

    private let client: SidecarClient
    /// True only for a session built by `posed(...)`.
    var isPosed = false
    private let previewGate: PreviewGate
    private let store: CredentialStore
    private let prober: CapabilityProber?
    private let executor: WorkExecutor?
    private let scratchCapacity: @Sendable () -> Int64
    private var capabilities: SidecarCapabilities
    private let sleep: @Sendable (TimeInterval) async throws -> Void

    private let persistConcurrency: @Sendable (Int) -> Void
    private var pairing: StoredPairing?
    private var heartbeatTask: Task<Void, Never>?
    private var jobTasks: [Int: Task<Void, Never>] = [:]
    /// Held while a job runs so macOS neither naps the app nor idles the machine to sleep under
    /// an encode. A lid close still sleeps the Mac; that path hands the job back first.
    private var activity: NSObjectProtocol?

    public init(
        client: SidecarClient = SidecarClient(),
        store: CredentialStore = KeychainCredentialStore(),
        capabilities: SidecarCapabilities = .provenToday(name: Host.current().localizedName ?? "Mac"),
        prober: CapabilityProber? = CapabilityProber(),
        previewGate: PreviewGate = PreviewGate(),
        executor: WorkExecutor? = nil,
        scratchCapacity: @escaping @Sendable () -> Int64 = {
            JobRunner.availableScratchBytes(at: JobRunner.defaultScratchRoot()) ?? 0
        },
        jobConcurrency: Int = UserDefaults.standard.object(forKey: "jobConcurrency") as? Int ?? 1,
        persistConcurrency: @escaping @Sendable (Int) -> Void = { UserDefaults.standard.set($0, forKey: "jobConcurrency") },
        sleep: @escaping @Sendable (TimeInterval) async throws -> Void = { seconds in
            try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
        }
    ) {
        self.client = client
        self.store = store
        self.capabilities = capabilities
        self.prober = prober
        self.previewGate = previewGate
        // Built here rather than as a default argument so the runner can read the same gate this
        // session hands to the menu.
        self.executor = executor ?? JobRunner(wantsPreviews: { [previewGate] in previewGate.isWanted })
        self.scratchCapacity = scratchCapacity
        self.jobConcurrency = Self.concurrencyRange.contains(jobConcurrency) ? jobConcurrency : 1
        self.persistConcurrency = persistConcurrency
        self.sleep = sleep
    }

    /// Changes how many jobs run at once. Takes effect on the next check-in; jobs already in
    /// flight are never stopped to fit a smaller number.
    public func setJobConcurrency(_ count: Int) {
        let clamped = min(max(count, Self.concurrencyRange.lowerBound), Self.concurrencyRange.upperBound)
        jobConcurrency = clamped
        persistConcurrency(clamped)
        if capabilities.maxConcurrency > 0 {
            capabilities.maxConcurrency = clamped
        }
    }

    /// Restores a previous pairing, if there is one, and starts checking in. Idempotent: the menu
    /// calls it every time it opens, and that must not restart a running check-in loop.
    ///
    /// The machine is probed again here, not only at pairing. Capabilities live in memory, so
    /// without this a relaunched app reported no encoders and zero concurrency — "drained" to the
    /// server — and never took work again until it was paired afresh.
    public func restore() {
        // A posed session is a still life for previews and `--render-menu`; restoring it would
        // reach for the Keychain and overwrite the very state being looked at.
        guard !isPosed else { return }
        guard pairing == nil else { return }
        guard let stored = try? store.load() else {
            status = .unpaired
            return
        }
        pairing = stored
        serverAddress = stored.serverAddress
        if let prober {
            Task { [weak self] in
                guard let self else { return }
                let proved = await prober.probe(name: self.capabilities.name, maxConcurrency: self.jobConcurrency)
                self.capabilities = proved
                self.startHeartbeat()
            }
        } else {
            startHeartbeat()
        }
    }

    /// Redeems a PIN. On success the credential is persisted immediately — the server returns it
    /// exactly once and cannot reissue it, so losing it here would mean pairing again.
    public func pair(serverAddress address: String, pin: String) async {
        status = .pairing
        serverAddress = address

        // Probed at the moment of pairing rather than at launch, so what the server records is what
        // this machine could do just now — a driver or an ffmpeg that changed since startup would
        // otherwise be reported as it was, and a capability the server believes but the machine
        // cannot honour is a job that can only fail.
        if let prober {
            capabilities = await prober.probe(name: capabilities.name, maxConcurrency: jobConcurrency)
        }
        capabilities.freeScratchBytes = max(0, scratchCapacity())

        do {
            let result = try await client.pair(
                serverAddress: address, pin: pin, capabilities: capabilities)

            let stored = StoredPairing(
                serverAddress: address, credential: result.credential, workerId: result.workerId)
            try store.save(stored)
            pairing = stored

            startHeartbeat()
        } catch let error as SidecarError {
            status = Self.describe(error)
        } catch {
            status = .pairingFailed(reason: error.localizedDescription)
        }
    }

    /// Forgets the pairing locally. This does not revoke anything server-side: only an operator
    /// can do that, and pretending otherwise would overstate what this app controls.
    public func unpair() {
        heartbeatTask?.cancel()
        heartbeatTask = nil
        // Jobs in flight are stopped too: cancelling the tasks terminates ffmpeg and the runner
        // hands each lease back, so the server can reassign rather than wait for it to lapse.
        for task in jobTasks.values { task.cancel() }
        jobTasks = [:]
        activeJobs = [:]
        filmStrips = [:]
        endActivity()
        try? store.clear()
        pairing = nil
        status = .unpaired
    }

    /// Stops the job in flight, if any, and waits until the lease has been handed back, so the
    /// server can reassign at once rather than after the lease lapses. The reason is what the
    /// menu shows afterwards, in place of the runner's generic cancellation wording.
    public func stopWork(because reason: String) async {
        let tasks = Array(jobTasks.values)
        guard !tasks.isEmpty else { return }
        for task in tasks { task.cancel() }
        for task in tasks { await task.value }
        if case let .released(jobId, _) = lastOutcome {
            lastOutcome = .released(jobId: jobId, reason: reason)
        }
    }

    /// The Mac is about to sleep. A sleeping worker cannot renew, so the lease would lapse and
    /// the job go back to the queue anyway, two minutes later and with the server unsure why.
    /// Handing it back now is the same outcome, sooner and explained. Check-ins stop until wake.
    public func systemWillSleep() async {
        heartbeatTask?.cancel()
        heartbeatTask = nil
        await stopWork(because: "This Mac went to sleep, so the job was handed back for another machine to take.")
        if pairing != nil, case .working = status {
            status = .unreachable(reason: "Asleep")
        }
    }

    /// Awake again: check in straight away rather than waiting out the old interval, so the
    /// Workers tab sees the machine back within seconds and it can take work again.
    public func systemDidWake() {
        guard pairing != nil else { return }
        startHeartbeat()
    }

    /// Called before the app exits. The heartbeat stops so the server sees a clean gap rather
    /// than a beat followed by silence, and any job is handed back rather than left to lapse.
    public func prepareToQuit() async {
        heartbeatTask?.cancel()
        heartbeatTask = nil
        await stopWork(because: "The sidecar was quit, so the job was handed back.")
    }

    private func beginActivity() {
        guard activity == nil else { return }
        activity = ProcessInfo.processInfo.beginActivity(
            options: [.userInitiated, .idleSystemSleepDisabled],
            reason: "Encoding for Optimisarr")
    }

    private func endActivity() {
        if let activity {
            ProcessInfo.processInfo.endActivity(activity)
        }
        activity = nil
    }

    private func startHeartbeat() {
        heartbeatTask?.cancel()
        heartbeatTask = Task { [weak self] in
            await self?.heartbeatLoop()
        }
    }

    private func heartbeatLoop() async {
        // The server tells us how often to check in, so the two stay in step. Until it has, use a
        // conservative interval rather than hammering it.
        var interval: TimeInterval = 30

        while !Task.isCancelled {
            guard let pairing else { return }

            do {
                capabilities.freeScratchBytes = max(0, scratchCapacity())
                let beat = try await client.heartbeat(
                    serverAddress: pairing.serverAddress,
                    credential: pairing.credential,
                    freeScratchBytes: capabilities.freeScratchBytes,
                    maxConcurrency: capabilities.maxConcurrency)

                interval = beat.heartbeatInterval
                if jobTasks.isEmpty {
                    status = .connected(workerId: beat.workerId, lastCheckIn: Date())
                }
                if !beat.draining {
                    await claimUpToCapacity(pairing: pairing, workerId: beat.workerId)
                }
            } catch SidecarError.credentialRejected {
                // Terminal. Retrying cannot help, and holding a dead secret on disk serves no
                // purpose, so drop it and tell the operator plainly.
                try? store.clear()
                self.pairing = nil
                status = .revoked
                return
            } catch let SidecarError.remoteWorkersDisabled(reason) {
                // Not terminal: an operator can switch the feature back on, and the credential is
                // still valid, so keep checking in rather than unpairing.
                status = .disabledOnServer(reason: reason)
            } catch let error as SidecarError {
                status = .unreachable(reason: Self.describe(error).shortReason)
            } catch {
                status = .unreachable(reason: error.localizedDescription)
            }

            try? await sleep(interval)
        }
    }

    /// Asks for work on each healthy check-in until every slot the operator allowed is filled.
    /// The server hands out one job per claim and holds the worker to the concurrency it
    /// reported, so this can never run more than the server believes it can.
    private func claimUpToCapacity(pairing: StoredPairing, workerId: Int) async {
        guard let executor, capabilities.maxConcurrency > 0 else { return }

        while jobTasks.count < capabilities.maxConcurrency {
            let assignment: Assignment?
            do {
                assignment = try await client.claim(
                    serverAddress: pairing.serverAddress, credential: pairing.credential)
            } catch {
                // A failed claim is not a failed check-in. The next beat asks again; a credential
                // problem surfaces through the heartbeat, which is the path that handles it.
                return
            }
            guard let assignment, jobTasks[assignment.jobId] == nil else { return }

            let jobId = assignment.jobId
            activeJobs[jobId] = .fetchingSource(received: 0, total: assignment.sourceBytes)
            refreshWorkingStatus()
            beginActivity()
            // The task holds the session for the job's duration, which is intended: a job is
            // stopped by cancelling this task (see unpair), never by the session quietly going
            // away under it.
            jobTasks[jobId] = Task { [weak self] in
                guard let self else { return }
                let outcome = await executor.execute(assignment, pairing: pairing) { progress in
                    Task { @MainActor in self.report(jobId: jobId, progress: progress) }
                } preview: { frame in
                    Task { @MainActor in self.report(jobId: jobId, frame: frame) }
                }
                self.finish(outcome, jobId: jobId, workerId: workerId)
            }
        }
    }

    private func report(jobId: Int, progress: JobProgress) {
        guard jobTasks[jobId] != nil else { return }
        activeJobs[jobId] = progress
        refreshWorkingStatus()
    }

    private func report(jobId: Int, frame: Data) {
        guard jobTasks[jobId] != nil else { return }
        filmStrips[jobId, default: FilmStrip()].append(frame)
        // Sampled here rather than on a timer of its own: the two readings then describe the same
        // instant, and nothing runs while no job does.
        gpu = GpuMonitor.sample()
    }

    /// Called by the menu as it opens and closes. Nothing is sampled while nobody is looking.
    public func setPreviewsWanted(_ wanted: Bool) {
        previewGate.set(wanted)
        if !wanted {
            filmStrips = [:]
            gpu = nil
        }
    }

    /// The menu bar shows one job; the earliest still running stands for the rest, and the menu
    /// itself lists them all.
    private func refreshWorkingStatus() {
        guard let first = activeJobs.keys.min(), let progress = activeJobs[first] else { return }
        status = .working(jobId: first, progress: progress)
    }

    private func finish(_ outcome: JobOutcome, jobId: Int, workerId: Int) {
        lastOutcome = outcome
        jobTasks[jobId] = nil
        activeJobs[jobId] = nil
        filmStrips[jobId] = nil
        if jobTasks.isEmpty {
            endActivity()
        } else {
            refreshWorkingStatus()
            return
        }
        if pairing != nil {
            status = .connected(workerId: workerId, lastCheckIn: Date())
        }
    }

    static func describe(_ error: SidecarError) -> SidecarStatus {
        switch error {
        case .invalidServerAddress:
            return .pairingFailed(reason: "That server address could not be understood.")
        case let .pairingRejected(reason):
            return .pairingFailed(reason: reason)
        case let .protocolIncompatible(reason):
            return .pairingFailed(reason: reason)
        case .credentialRejected:
            return .revoked
        case let .remoteWorkersDisabled(reason):
            return .disabledOnServer(reason: reason)
        case let .unexpectedResponse(status):
            return .pairingFailed(reason: "The server replied unexpectedly (HTTP \(status)).")
        case let .unreachable(description):
            return .pairingFailed(reason: description)
        case let .leaseLost(reason), let .transferFailed(reason), let .deliveryRefused(reason):
            // Job-time errors never reach pairing or check-in, but the mapping stays total so a
            // new case cannot be forgotten silently.
            return .unreachable(reason: reason)
        case let .uploadOffsetMismatch(serverHolds):
            return .unreachable(reason: "The upload lost its place; the server holds \(serverHolds) bytes.")
        }
    }
}

extension SidecarStatus {
    /// A short line for the menu bar, without the surrounding case.
    var shortReason: String {
        switch self {
        case let .pairingFailed(reason): return reason
        case let .unreachable(reason): return reason
        case let .disabledOnServer(reason): return reason
        default: return ""
        }
    }

    /// What the menu bar shows at a glance.
    public var summary: String {
        switch self {
        case .unpaired: return "Not paired"
        case .pairing: return "Pairing…"
        case .connected: return "Connected"
        case let .working(jobId, _): return "Encoding job #\(jobId)"
        case .unreachable: return "Server unreachable"
        case .revoked: return "Access revoked"
        case .disabledOnServer: return "Turned off on the server"
        case .pairingFailed: return "Pairing failed"
        }
    }
}

public extension SidecarSession {
    /// A session posed in a given state, for SwiftUI previews and for the app's own
    /// `--render-menu` mode.
    ///
    /// The menu is the whole product here and it is fiddly to judge from code, but every state
    /// worth looking at — mid-transfer, encoding with a strip of frames, revoked — needs a paired
    /// server and a running job to reach for real. Posing one is how the layout gets reviewed
    /// without that, and it is why the published properties are settable from here and nowhere
    /// else.
    static func posed(
        status: SidecarStatus,
        serverAddress: String = "https://optimisarr.pownet.uk",
        activeJobs: [Int: JobProgress] = [:],
        filmStrips: [Int: FilmStrip] = [:],
        gpu: GpuUsage? = nil,
        lastOutcome: JobOutcome? = nil
    ) -> SidecarSession {
        let session = SidecarSession(prober: nil, executor: nil)
        session.isPosed = true
        session.status = status
        session.serverAddress = serverAddress
        session.activeJobs = activeJobs
        session.filmStrips = filmStrips
        session.gpu = gpu
        session.lastOutcome = lastOutcome
        return session
    }
}
