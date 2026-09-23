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

    /// How fast each job's current transfer is moving, in bytes per second. Absent while a job is
    /// encoding or measuring, which move no bytes, and until there are two reports to compare.
    @Published public internal(set) var transferRates: [Int: Double] = [:]

    /// What each running job is working on, keyed by job id. Empty when the server did not say.
    @Published public internal(set) var jobTitles: [Int: String] = [:]

    /// The recent frames each running job was seen encoding. Only collected while the menu is
    /// open, and dropped as soon as the job ends.
    @Published public internal(set) var filmStrips: [Int: FilmStrip] = [:]

    /// The GPU reading taken alongside the last preview, when this Mac publishes one.
    @Published public internal(set) var gpu: GpuUsage?

    /// How busy this Mac is, for the menu to draw beside a running job.
    ///
    /// Sampled on its own short timer rather than alongside a preview frame. Tying it to frames
    /// meant the figure only moved when one arrived — and stopped entirely when previews were off,
    /// which they were in every shipped build.
    @Published public internal(set) var cpu: Double?

    /// Phase of the production Precession frame sequence, advanced only while a job is running.
    @Published public internal(set) var spin: Double = 0

    /// How many jobs this Mac takes at once. Chosen by the operator, reported to the server on
    /// every check-in, and the ceiling the claim loop fills up to.
    @Published public private(set) var jobConcurrency: Int

    @Published public private(set) var isPaused = false

    /// Pausing only gates new claims; lease renewal and work already held continue normally.
    public func setPaused(_ paused: Bool) {
        guard !shutdown.armed else { return }
        isPaused = paused
    }

    @Published public private(set) var shutdown = ShutdownCountdown()
    private var pausedBeforeShutdown = false
    private var lastDrainedHeartbeat: Date?
    private var unconfirmedResult = false
    private var shutdownTask: Task<Void, Never>?
    private let requestShutdown: @Sendable () async throws -> Void

    public func armShutdown() {
        guard isPaired, !shutdown.armed else { return }
        pausedBeforeShutdown = isPaused
        shutdown.arm()
        lastDrainedHeartbeat = nil
        // Cancel a pending claim/check-in and immediately report zero capacity to the server.
        startHeartbeat()
        shutdownTask = Task { [weak self] in
            while let self, !Task.isCancelled, self.shutdown.armed {
                await self.advanceShutdown()
                try? await Task.sleep(for: .seconds(1))
            }
        }
    }

    public func cancelShutdown() {
        guard shutdown.canCancel else { return }
        disarmShutdown(restartHeartbeat: true)
    }

    private func disarmShutdown(restartHeartbeat: Bool) {
        shutdownTask?.cancel()
        shutdownTask = nil
        shutdown = ShutdownCountdown()
        lastDrainedHeartbeat = nil
        isPaused = pausedBeforeShutdown
        if restartHeartbeat { startHeartbeat() }
    }

    private func advanceShutdown() async {
        let ready = lastDrainedHeartbeat.map { Date().timeIntervalSince($0) < 35 } == true
            && { if case .connected = status { return true }; return false }()
        let unreachable: Bool
        if case .unreachable = status { unreachable = true } else { unreachable = false }
        guard shutdown.evaluate(at: Date(), ready: ready, activeJobs: jobTasks.count,
                                unconfirmed: unconfirmedResult, serverUnreachable: unreachable) else { return }
        guard let pairing else {
            shutdown.deferShutdown("No pairing is available; shutdown is blocked.")
            return
        }
        do {
            _ = try await client.heartbeat(
                serverAddress: pairing.serverAddress, credential: pairing.credential,
                freeScratchBytes: max(0, scratchCapacity()), maxConcurrency: 0,
                capabilities: capabilities, load: load.sample())
        } catch {
            if shutdown.armed {
                lastDrainedHeartbeat = nil
                let reason = "Final shutdown check failed: \(error.localizedDescription)"
                status = .unreachable(reason: reason)
                shutdown.deferShutdown("\(reason). Shutdown is blocked until check-ins recover.")
            }
            return
        }
        guard shutdown.armed, jobTasks.isEmpty, !unconfirmedResult else { return }
        lastDrainedHeartbeat = Date()
        guard shutdown.begin() else { return }
        do {
            try await requestShutdown()
        } catch {
            if shutdown.armed { shutdown.fail("macOS could not shut down: \(error.localizedDescription)") }
        }
    }

    public static let concurrencyRange = 1...4

    private let client: SidecarClient
    /// True only for a session built by `posed(...)`.
    var isPosed = false
    private let previewGate: PreviewGate
    /// Drives the menu bar's spin and the load figures. Runs only while there is work, because a
    /// timer redrawing a menu bar icon for hours on a laptop is a cost with nothing to show for it
    /// when the machine is idle.
    private var uiTicker: Task<Void, Never>?
    private let uiLoad = MachineLoadSampler()
    /// The figures the menu is showing, which outlive the readings that could not be taken. See
    /// `MachineLoadDisplay`.
    private var uiDisplay = MachineLoadDisplay()
    /// One meter per job, reset when the job changes stage so a download's rate never colours an
    /// upload's.
    private var rateMeters: [Int: RateMeter] = [:]
    private let store: CredentialStore
    /// Sampled at each check-in. Its own sampler rather than the job runner's: see
    /// `MachineLoadSampler`.
    private let load = MachineLoadSampler()
    /// The outstanding credential load, if one is running. Held so repeated menu opens do not
    /// stack reads of the same item, and so a caller that must decide what to show next can wait
    /// for the answer instead of racing it.
    private var restoreTask: Task<Void, Never>?
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
        /// The settings a job runs under, when this session is building its own runner. Ignored
        /// when `executor` is supplied, which only tests do.
        settingsSnapshot: SettingsSnapshot? = nil,
        executor: WorkExecutor? = nil,
        scratchCapacity: @escaping @Sendable () -> Int64 = {
            JobRunner.availableScratchBytes(at: JobRunner.defaultScratchRoot()) ?? 0
        },
        jobConcurrency: Int = UserDefaults.standard.object(forKey: "jobConcurrency") as? Int ?? 1,
        persistConcurrency: @escaping @Sendable (Int) -> Void = { UserDefaults.standard.set($0, forKey: "jobConcurrency") },
        sleep: @escaping @Sendable (TimeInterval) async throws -> Void = { seconds in
            try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
        },
        requestShutdown: @escaping @Sendable () async throws -> Void = { try await SystemShutdown.request() }
    ) {
        self.client = client
        self.store = store
        self.capabilities = capabilities
        self.prober = prober
        self.previewGate = previewGate
        // Built here rather than as a default argument so the runner can read the same gate this
        // session hands to the menu.
        self.executor = executor ?? JobRunner(
            wantsPreviews: { [previewGate] in previewGate.isWanted },
            settings: settingsSnapshot ?? SettingsSnapshot())
        self.scratchCapacity = scratchCapacity
        self.jobConcurrency = Self.concurrencyRange.contains(jobConcurrency) ? jobConcurrency : 1
        self.persistConcurrency = persistConcurrency
        self.sleep = sleep
        self.requestShutdown = requestShutdown
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

        // Before anything is claimed, which is the one moment this process is certain to hold no
        // lease. A job removes its own working directory in a `defer` — that covers every way a
        // job can end and none of the ways a process can, so a force quit or a crash leaves
        // gigabytes behind with nothing that would ever remove them. Left alone while still being
        // written to, in case another copy of the app is mid-job. See `OrphanedScratch`.
        let reclaimed = OrphanedScratch.sweep(in: JobRunner.defaultScratchRoot())
        if reclaimed > 0 {
            SidecarLog.storage.notice(
                "Removed \(reclaimed) working director\(reclaimed == 1 ? "y" : "ies") left by an earlier run")
        }

        // The menu calls this every time it opens, and the load below is now asynchronous — without
        // this, opening the menu twice in quick succession would start a second read of the same
        // item while the first was still outstanding.
        guard restoreTask == nil else { return }

        restoreTask = Task { [weak self] in
            guard let self else { return }
            // Off this actor entirely. Reading the Keychain can block — on a legacy item written by
            // a build whose signature no longer matches, indefinitely — and this class is
            // `@MainActor`, so doing it inline freezes the app. A `Task { @MainActor in … }` around
            // the call does not help and reads as though it does: it is already the main actor, so
            // it blocks in exactly the same way once it starts. A windowless menu-bar app that
            // freezes here has no window to show for it and looks simply dead, which is what
            // 0.1.7 did.
            let store = self.store
            let stored = await Task.detached(priority: .userInitiated) { try? store.load() }.value
            self.finishRestoring(stored)
        }
    }

    /// Whether a stored pairing has been loaded.
    ///
    /// Not the same question as `status`, and the difference matters: a restored pairing leaves the
    /// status alone until the first check-in answers, so a freshly launched app that *is* paired
    /// still reads as `.unpaired` for a second or two. Asking the status instead is how the app
    /// came to open its fallback window on every single launch.
    public var isPaired: Bool { pairing != nil }

    /// Restores, and waits for the stored credential to have been looked for. For a caller that
    /// must decide what to show next — the launch path, which opens the pairing window when there
    /// is nothing to restore — and would otherwise read `status` before the answer exists.
    public func restoreAndSettle() async {
        restore()
        let outstanding = restoreTask
        await outstanding?.value
    }

    private func finishRestoring(_ stored: StoredPairing?) {
        restoreTask = nil
        guard let stored else {
            SidecarLog.session.info("No stored pairing; waiting to be paired")
            status = .unpaired
            return
        }
        pairing = stored
        serverAddress = stored.serverAddress
        SidecarLog.session.info("Restored pairing with worker \(stored.workerId, privacy: .public)")
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
        if shutdown.armed { disarmShutdown(restartHeartbeat: false) }
        heartbeatTask?.cancel()
        heartbeatTask = nil
        // Jobs in flight are stopped too: cancelling the tasks terminates ffmpeg and the runner
        // hands each lease back, so the server can reassign rather than wait for it to lapse.
        for task in jobTasks.values { task.cancel() }
        jobTasks = [:]
        activeJobs = [:]
        jobTitles = [:]
        transferRates = [:]
        rateMeters = [:]
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
        if shutdown.armed {
            lastDrainedHeartbeat = nil
            _ = shutdown.evaluate(at: Date(), ready: false, activeJobs: jobTasks.count)
        }
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
        if shutdown.armed { disarmShutdown(restartHeartbeat: false) }
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
                let reportingDrain = shutdown.armed
                let beat = try await client.heartbeat(
                    serverAddress: pairing.serverAddress,
                    credential: pairing.credential,
                    freeScratchBytes: capabilities.freeScratchBytes,
                    maxConcurrency: reportingDrain ? 0 : capabilities.maxConcurrency,
                    capabilities: capabilities,
                    // So an idle Mac still says how busy it is. While a job runs, its lease
                    // renewals carry a fresher figure at a much shorter cadence.
                    load: load.sample())

                interval = beat.heartbeatInterval
                if reportingDrain && shutdown.armed { lastDrainedHeartbeat = Date() }
                if jobTasks.isEmpty {
                    status = .connected(workerId: beat.workerId, lastCheckIn: Date())
                }
                if !beat.draining && !isPaused && !shutdown.armed {
                    await claimUpToCapacity(pairing: pairing, workerId: beat.workerId)
                }
            } catch SidecarError.credentialRejected {
                if shutdown.armed { disarmShutdown(restartHeartbeat: false) }
                lastDrainedHeartbeat = nil
                // Terminal. Retrying cannot help, and holding a dead secret on disk serves no
                // purpose, so drop it and tell the operator plainly.
                try? store.clear()
                self.pairing = nil
                status = .revoked
                return
            } catch let SidecarError.remoteWorkersDisabled(reason) {
                lastDrainedHeartbeat = nil
                // Not terminal: an operator can switch the feature back on, and the credential is
                // still valid, so keep checking in rather than unpairing.
                status = .disabledOnServer(reason: reason)
            } catch let error as SidecarError {
                lastDrainedHeartbeat = nil
                status = .unreachable(reason: Self.describe(error).shortReason)
            } catch {
                lastDrainedHeartbeat = nil
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

        while !isPaused && !shutdown.armed && jobTasks.count < capabilities.maxConcurrency {
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
            if isPaused || shutdown.armed {
                do {
                    try await client.release(serverAddress: pairing.serverAddress,
                                             credential: pairing.credential, leaseId: assignment.leaseId)
                } catch {
                    // The claim may have returned after shutdown was armed. A failed hand-back
                    // leaves a possibly held lease, so the countdown must stay blocked.
                    let reason = "Job \(assignment.jobId): hand-back was not acknowledged: \(error.localizedDescription)"
                    unconfirmedResult = true
                    lastOutcome = .unconfirmed(jobId: assignment.jobId, reason: reason)
                    status = .unreachable(reason: reason)
                }
                return
            }

            let jobId = assignment.jobId
            activeJobs[jobId] = .fetchingSource(received: 0, total: assignment.sourceBytes)
            jobTitles[jobId] = assignment.title
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
        let previous = activeJobs[jobId]
        activeJobs[jobId] = progress
        updateRate(jobId: jobId, from: previous, to: progress)
        refreshWorkingStatus()
    }

    /// Keeps the transfer rate for the two stages that move bytes.
    private func updateRate(jobId: Int, from previous: JobProgress?, to progress: JobProgress) {
        // A stage change starts a new measurement: a download's rate says nothing about an upload,
        // and carrying it over would show a figure for the wrong thing.
        if previous?.isSameStage(as: progress) != true {
            rateMeters[jobId] = RateMeter()
            transferRates[jobId] = nil
        }

        guard let moved = progress.transferredBytes else { return }
        var meter = rateMeters[jobId] ?? RateMeter()
        let rate = meter.observe(moved, at: Date())
        rateMeters[jobId] = meter
        transferRates[jobId] = rate
    }

    private func log(_ status: SidecarStatus) {
        switch status {
        case .revoked, .pairingFailed, .unreachable, .disabledOnServer:
            SidecarLog.session.error("Status: \(status.summary, privacy: .public)")
        default:
            SidecarLog.session.info("Status: \(status.summary, privacy: .public)")
        }
    }

    private func report(jobId: Int, frame: Data) {
        guard jobTasks[jobId] != nil else { return }
        filmStrips[jobId, default: FilmStrip()].append(frame)
    }

    /// Called by the menu as it opens and closes. Frames are only extracted while somebody is
    /// looking, because pulling one costs an ffmpeg invocation per sample.
    ///
    /// Load is not gated this way. It is cheap to read, it drives the menu bar mark as well as the
    /// menu, and tying it to the preview gate is how the GPU figure came to be invisible: it was
    /// sampled beside a preview frame, and previews were never switched on in a shipped build.
    public func setPreviewsWanted(_ wanted: Bool) {
        previewGate.set(wanted)
        menuIsOpen = wanted
        if !wanted {
            filmStrips = [:]
        }
        // An idle Mac has a load worth showing while someone is looking at it. The ticker ran only
        // while a job did, so the meters were empty in the one state where "is this machine
        // actually free?" is the question being asked.
        setTickerRunning(!activeJobs.isEmpty || menuIsOpen)
    }

    /// Whether the menu is on screen. The load figures cost a reading of the kernel's counters
    /// every second, which is not worth doing for a window nobody has open.
    private var menuIsOpen = false

    /// How often the menu bar mark is redrawn while work is running.
    private static let spinTicksPerSecond = 10.0

    /// Starts or stops the short timer behind the menu bar's spin and the load figures.
    ///
    /// Only while a job is running. A timer redrawing a menu bar icon on a laptop for hours is a
    /// cost with nothing to show for it once the machine is idle, and an idle Mac has no load worth
    /// watching either.
    private func setTickerRunning(_ running: Bool) {
        guard running != (uiTicker != nil) else { return }

        guard running else {
            uiTicker?.cancel()
            uiTicker = nil
            uiDisplay.clear()
            cpu = nil
            gpu = nil
            spin = 0
            return
        }

        uiTicker = Task { [weak self] in
            // The icon wants a smooth cadence; the load figures want a coarse one. A busy fraction
            // is measured between two readings of the kernel's tick counters, and ten times a
            // second is often too little time for them to move at all — which is precisely the
            // interval that cannot be answered, so the meters spent their lives blanking. Sampling
            // once a second gives the counters something to say at a tenth of the icon cadence.
            let ticksPerSample = Int(Self.spinTicksPerSecond)
            var tick = 0
            while !Task.isCancelled {
                guard let self else { return }
                let hasWork = !self.activeJobs.isEmpty
                if !hasWork || tick % ticksPerSample == 0 {
                    self.uiDisplay.observe(self.uiLoad.sample())
                    self.cpu = self.uiDisplay.cpu
                    if let device = self.uiDisplay.gpu {
                        self.gpu = GpuUsage(device: device, memoryInUse: self.gpu?.memoryInUse ?? 0)
                    }
                }
                // One motion cycle in 8.8 seconds, matching the web icon. The
                // ticker also runs for the load meters whenever the menu is open, and turning the
                // mark from that made an idle Mac look busy for exactly as long as somebody was
                // looking at it — which is the one moment the mark has to be honest.
                if !hasWork {
                    if self.spin != 0 { self.spin = 0 }
                } else {
                    self.spin += 1.0 / (8.8 * Self.spinTicksPerSecond)
                }
                tick &+= 1
                try? await Task.sleep(for: .milliseconds(hasWork ? Int(1000.0 / Self.spinTicksPerSecond) : 1000))
            }
        }
    }

    /// The menu bar shows one job; the earliest still running stands for the rest, and the menu
    /// itself lists them all.
    private func refreshWorkingStatus() {
        // Driven from the one place that knows whether anything is running, so the menu bar stops
        // spinning the moment the last job ends rather than whenever someone next opens the menu.
        setTickerRunning(!activeJobs.isEmpty || menuIsOpen)
        guard let first = activeJobs.keys.min(), let progress = activeJobs[first] else { return }
        status = .working(jobId: first, progress: progress)
    }

    private func finish(_ outcome: JobOutcome, jobId: Int, workerId: Int) {
        lastOutcome = outcome
        if case .unconfirmed = outcome { unconfirmedResult = true }
        jobTasks[jobId] = nil
        activeJobs[jobId] = nil
        setTickerRunning(!activeJobs.isEmpty || menuIsOpen)
        jobTitles[jobId] = nil
        transferRates[jobId] = nil
        rateMeters[jobId] = nil
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
        // The stage, not a fixed word: "Encoding" while a source is still downloading is simply
        // untrue, and this line is the one thing visible from the menu bar without opening it.
        case let .working(jobId, progress): return "\(progress.summary) job #\(jobId)"
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
        serverAddress: String = "https://optimisarr.example.com",
        activeJobs: [Int: JobProgress] = [:],
        jobTitles: [Int: String] = [:],
        transferRates: [Int: Double] = [:],
        filmStrips: [Int: FilmStrip] = [:],
        gpu: GpuUsage? = nil,
        lastOutcome: JobOutcome? = nil,
        shutdown: ShutdownCountdown = ShutdownCountdown()
    ) -> SidecarSession {
        let session = SidecarSession(prober: nil, executor: nil)
        session.isPosed = true
        session.status = status
        session.serverAddress = serverAddress
        session.activeJobs = activeJobs
        session.jobTitles = jobTitles
        session.transferRates = transferRates
        session.filmStrips = filmStrips
        session.gpu = gpu
        session.lastOutcome = lastOutcome
        session.shutdown = shutdown
        return session
    }
}
