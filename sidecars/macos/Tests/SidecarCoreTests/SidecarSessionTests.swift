import Foundation
import Testing
@testable import SidecarCore

private final class SessionCapabilityHelper: NativeQualityExchanging, @unchecked Sendable {
    private let gate = NSLock()
    private var began = false, ended = false
    var started: Bool { gate.withLock { began } }
    var stopped: Bool { gate.withLock { ended } }
    func exchange(request: Data, expectedJobId: Int?, report: @escaping @Sendable (NativeQualityEvent) -> Void) async throws -> NativeQualityReply {
        gate.withLock { began = true }
        defer { gate.withLock { ended = true } }
        try await Task.sleep(for: .seconds(60))
        throw NativeQualityHostError()
    }
}

private final class NativeResourceSequence: @unchecked Sendable {
    private let gate = NSLock()
    private var count: Int64 = 0
    func refresh(_ capabilities: SidecarCapabilities) -> SidecarCapabilities {
        gate.withLock {
            count += 1
            var fresh = capabilities
            fresh.perceptual?.availableMemoryBytes = count * 100
            fresh.perceptual?.scratchUsesMemory = false
            fresh.freeScratchBytes = 1000
            return fresh
        }
    }
}

/// A transport that answers differently per call, so a whole lifecycle can be walked: pair, check
/// in, get revoked.
final class ScriptedTransport: HTTPTransport, @unchecked Sendable {
    struct Reply {
        let status: Int
        let json: [String: Any]
    }

    private let lock = NSLock()
    private var replies: [Reply]
    private(set) var callCount = 0
    /// How many times work was asked for, whatever the scripted answer was.
    private(set) var claims = 0
    private(set) var releases = 0
    var onClaim: (@Sendable () async -> Void)?
    var onClaimAfterReply: (@Sendable () async -> Void)?
    var releaseStatusOverride: Int?
    private(set) var heartbeatScratchBytes: [Int64] = []
    private(set) var heartbeatCapacities: [Int] = []
    private var nativeMemoryReports: [Int64] = []
    var reportedNativeMemory: [Int64] { lock.withLock { nativeMemoryReports } }

    init(_ replies: [Reply]) {
        self.replies = replies
    }

    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        if request.url?.path.hasSuffix("/claim") == true { await onClaim?() }
        let reply: Reply = lock.withLock {
            callCount += 1
            if let body = request.httpBody,
               let json = try? JSONSerialization.jsonObject(with: body) as? [String: Any],
               let proof = json["perceptual"] as? [String: Any],
               let memory = proof["availableMemoryBytes"] as? Int64 {
                nativeMemoryReports.append(memory)
            }
            if request.url?.path.hasSuffix("/release") == true { releases += 1 }
            if request.url?.path.hasSuffix("/claim") == true { claims += 1 }
            if request.url?.path.hasSuffix("/heartbeat") == true,
               let body = request.httpBody,
               let json = try? JSONSerialization.jsonObject(with: body) as? [String: Any],
               let bytes = (json["freeScratchBytes"] as? NSNumber)?.int64Value {
                heartbeatScratchBytes.append(bytes)
                if let capacity = (json["maxConcurrency"] as? NSNumber)?.intValue {
                    heartbeatCapacities.append(capacity)
                }
            }
            if request.url?.path.hasSuffix("/release") == true,
               let releaseStatusOverride { return Reply(status: releaseStatusOverride, json: [:]) }
            return replies.count > 1 ? replies.removeFirst() : replies[0]
        }
        if request.url?.path.hasSuffix("/claim") == true { await onClaimAfterReply?() }
        let data = (try? JSONSerialization.data(withJSONObject: reply.json)) ?? Data()
        let response = HTTPURLResponse(
            url: request.url!, statusCode: reply.status, httpVersion: nil, headerFields: nil)!
        return (data, response)
    }

    func download(_ request: URLRequest, to destination: URL) async throws -> HTTPURLResponse {
        let (data, response) = try await send(request)
        try data.write(to: destination)
        return response
    }

    func upload(_ request: URLRequest, fromFile file: URL) async throws -> (Data, HTTPURLResponse) {
        try await send(request)
    }
}

/// A job that runs until it is cancelled, then reports what the real runner would after handing
/// the lease back. Lets the sleep and quit paths be walked without ffmpeg or a server.
final class HangingExecutor: WorkExecutor, @unchecked Sendable {
    private let lock = NSLock()
    private(set) var started = false
    private(set) var startedCount = 0
    private(set) var cancelled = false

    func execute(
        _ assignment: Assignment, pairing: StoredPairing,
        progress: @escaping @Sendable (JobProgress) -> Void,
        preview: @escaping @Sendable (Data) -> Void,
        storage: @escaping @Sendable (WorkStorage) -> Void
    ) async -> JobOutcome {
        lock.withLock { started = true; startedCount += 1 }
        while !Task.isCancelled {
            try? await Task.sleep(nanoseconds: 2_000_000)
        }
        lock.withLock { cancelled = true }
        return .released(jobId: assignment.jobId, reason: "The job was cancelled on this machine.")
    }
}

final class Persisted: @unchecked Sendable {
    private let lock = NSLock()
    private(set) var value: Int?
    func set(_ new: Int) { lock.withLock { value = new } }
}

@MainActor
@Suite("Session lifecycle")
struct SidecarSessionTests {
    private func session(
        _ replies: [ScriptedTransport.Reply],
        store: CredentialStore = InMemoryCredentialStore()
    ) -> (SidecarSession, InMemoryCredentialStore?) {
        let transport = ScriptedTransport(replies)
        let session = SidecarSession(
            client: SidecarClient(transport: transport),
            store: store,
            capabilities: .provenToday(name: "Test"),
            // No prober: the capabilities above are the test's statement of what this machine can do.
            prober: nil,
            persistConcurrency: { _ in },
            // Collapses the wait so a check-in loop can be walked without real time passing.
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) }
        )
        return (session, store as? InMemoryCredentialStore)
    }

    @Test("pairing stores the credential immediately")
    func pairingPersists() async throws {
        let store = InMemoryCredentialStore()
        let (session, _) = session([
            .init(status: 200, json: ["workerId": 4, "credential": "kept", "protocolVersion": 1]),
            .init(status: 200, json: ["workerId": 4, "protocolVersion": 1, "heartbeatIntervalSeconds": 30]),
        ], store: store)

        await session.pair(serverAddress: "localhost:8787", pin: "12345678")

        // The server returns the credential exactly once, so it must reach storage before anything
        // else can go wrong.
        let stored = try store.load()
        #expect(stored?.credential == "kept")
        #expect(stored?.workerId == 4)
    }

    @Test("a rejected PIN leaves nothing stored and explains itself")
    func rejectedPairing() async throws {
        let store = InMemoryCredentialStore()
        let (session, _) = session([
            .init(status: 401, json: ["error": "That pairing code has expired. Generate a new one."]),
        ], store: store)

        await session.pair(serverAddress: "localhost:8787", pin: "00000000")

        #expect(try store.load() == nil)
        #expect(session.status == .pairingFailed(reason: "That pairing code has expired. Generate a new one."))
    }

    @Test("a revoked credential is discarded rather than retried forever")
    func revocationIsTerminal() async throws {
        let store = InMemoryCredentialStore(
            stored: StoredPairing(serverAddress: "localhost:8787", credential: "stale", workerId: 9))
        let (session, _) = session([
            .init(status: 401, json: ["error": "Unknown or revoked worker credential."]),
        ], store: store)

        session.restore()
        try await waitFor { session.status == .revoked }

        // Holding a dead secret on disk serves nobody, and retrying cannot recover it — only
        // pairing again can.
        #expect(try store.load() == nil)
        #expect(session.status == .revoked)
    }

    @Test("a credential store that blocks does not block the caller")
    func aBlockingStoreDoesNotFreezeTheApp() async throws {
        // The real Keychain blocks indefinitely on a legacy item written by a build whose signature
        // no longer matches — it waits on a dialog the operator may never see. `SidecarSession` is
        // `@MainActor` and the app has no window and no Dock icon, so a read left on this actor is
        // an app that starts and then freezes with nothing whatever to show for it. That shipped in
        // 0.1.7. The read therefore has to happen somewhere else, and `restore()` has to come back
        // to its caller immediately.
        let store = BlockingCredentialStore(
            blockFor: .milliseconds(400),
            stored: StoredPairing(serverAddress: "localhost:8787", credential: "stale", workerId: 9))
        let (session, _) = session([
            .init(status: 401, json: ["error": "Unknown or revoked worker credential."]),
        ], store: store)

        let started = ContinuousClock.now
        session.restore()
        let returnedAfter = ContinuousClock.now - started

        // Returned while the read was still outstanding, rather than after it.
        #expect(returnedAfter < .milliseconds(200))
        // And still arrives at the right answer once the read finishes.
        try await waitFor { session.status == .revoked }
    }

    @Test("the feature being switched off keeps the credential and keeps trying")
    func disabledIsRecoverable() async throws {
        let store = InMemoryCredentialStore(
            stored: StoredPairing(serverAddress: "localhost:8787", credential: "good", workerId: 2))
        let (session, _) = session([
            .init(status: 403, json: ["error": "Remote workers are turned off."]),
        ], store: store)

        session.restore()
        try await waitFor {
            if case .disabledOnServer = session.status { return true }
            return false
        }

        // Distinct from revoked on purpose: an operator can switch this back on, and the
        // credential is still perfectly good, so throwing it away would force a needless re-pair.
        #expect(try store.load()?.credential == "good")
    }

    @Test("unpairing forgets locally without claiming to have revoked anything")
    func unpairing() async throws {
        let store = InMemoryCredentialStore(
            stored: StoredPairing(serverAddress: "localhost:8787", credential: "c", workerId: 1))
        let (session, _) = session([
            .init(status: 200, json: ["workerId": 1, "protocolVersion": 1, "heartbeatIntervalSeconds": 30]),
        ], store: store)

        session.restore()
        session.unpair()

        #expect(try store.load() == nil)
        #expect(session.status == .unpaired)
    }

    @Test("a check-in success reports connected")
    func connects() async throws {
        let store = InMemoryCredentialStore(
            stored: StoredPairing(serverAddress: "localhost:8787", credential: "c", workerId: 11))
        let (session, _) = session([
            .init(status: 200, json: ["workerId": 11, "protocolVersion": 1, "heartbeatIntervalSeconds": 30]),
        ], store: store)

        session.restore()
        try await waitFor {
            if case .connected = session.status { return true }
            return false
        }

        #expect(session.status.summary == "Connected")
    }

    @Test("every check-in reports current work-volume capacity rather than the launch-time value")
    func heartbeatRefreshesScratchCapacity() async throws {
        let store = InMemoryCredentialStore(
            stored: StoredPairing(serverAddress: "localhost:8787", credential: "c", workerId: 11))
        let transport = ScriptedTransport([
            .init(status: 200, json: ["workerId": 11, "protocolVersion": 1, "heartbeatIntervalSeconds": 30]),
        ])
        let session = SidecarSession(
            client: SidecarClient(transport: transport),
            store: store,
            capabilities: SidecarCapabilities(
                name: "Test", videoEncoders: ["libx265"], freeScratchBytes: 999, maxConcurrency: 1),
            prober: nil,
            executor: nil,
            scratchCapacity: { 321 },
            persistConcurrency: { _ in },
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        session.restore()
        try await waitFor { !transport.heartbeatScratchBytes.isEmpty }

        #expect(transport.heartbeatScratchBytes.first == 321)
        session.unpair()
    }

    @Test("pairing and every heartbeat refresh native resources independently of tool qualification")
    func nativeResourcesStayFresh() async throws {
        let transport = ScriptedTransport([
            .init(status: 200, json: ["workerId": 11, "credential": "fixture", "protocolVersion": 10, "heartbeatIntervalSeconds": 30]),
        ])
        let proof = try #require(WorkerPerceptualCapabilities(proof: NativeQualityCapabilityTests.proof))
        let refresh = NativeResourceSequence()
        let session = SidecarSession(client: SidecarClient(transport: transport), store: InMemoryCredentialStore(),
            capabilities: SidecarCapabilities(name: "Fixture", perceptual: proof), prober: nil,
            nativeResources: refresh.refresh, persistConcurrency: { _ in },
            sleep: { _ in try await Task.sleep(for: .milliseconds(5)) })
        await session.pair(serverAddress: "localhost:8787", pin: "fixture")
        try await waitFor { transport.reportedNativeMemory.count >= 3 }
        session.unpair()
        #expect(Array(transport.reportedNativeMemory.prefix(3)) == [100, 200, 300])
    }

    @Test("unpair, sleep, quit and caller cancellation stop native qualification before any heartbeat", arguments: ["restore-unpair", "pair-unpair", "restore-sleep", "pair-sleep", "restore-quit", "pair-quit", "pair-cancel"])
    func unpairCancelsNativeQualification(action: String) async throws {
        let helper = SessionCapabilityHelper()
        let native = NativeQualityCapabilityProber(tools: NativeQualityCapabilityTests.tools, helper: helper)
        let prober = CapabilityProber(ffmpeg: NativeQualityCapabilityTests.tools.ffmpeg,
            runner: ScriptedRunner(["encoders": (0, "")]), native: native)
        let transport = ScriptedTransport([.init(status: 200, json: [:])])
        let store = InMemoryCredentialStore(stored: StoredPairing(serverAddress: "localhost:8787", credential: "fixture", workerId: 11))
        let session = SidecarSession(client: SidecarClient(transport: transport), store: store, prober: prober,
            persistConcurrency: { _ in })
        let pairingTask: Task<Void, Never>?
        if action.hasPrefix("pair-") {
            pairingTask = Task { await session.pair(serverAddress: "localhost:8787", pin: "fixture") }
        } else { session.restore(); pairingTask = nil }
        try await waitFor { helper.started }
        if action.hasSuffix("unpair") { session.unpair() }
        else if action.hasSuffix("sleep") { await session.systemWillSleep() }
        else if action.hasSuffix("cancel") { pairingTask?.cancel() }
        else { await session.prepareToQuit() }
        try await waitFor { helper.stopped }
        await pairingTask?.value
        if action.hasSuffix("unpair") { #expect(!session.isPaired) }
        #expect(transport.callCount == 0)
        session.unpair()
    }

    @Test("startup, unpair and caller cancellation own working-file recovery before network activity", arguments: ["restore-unpair", "pair-unpair", "pair-cancel"])
    func nativeRecoveryOwnership(action: String) async throws {
        let helper = ScratchRecoveryHelper(hang: true)
        let recovery = NativeScratchRecoveryService(tools: NativeQualityCapabilityTests.tools, helper: helper)
        let transport = ScriptedTransport([.init(status: 200, json: [:])])
        let store = InMemoryCredentialStore(stored: StoredPairing(serverAddress: "localhost:8787", credential: "fixture", workerId: 11))
        let session = SidecarSession(client: SidecarClient(transport: transport), store: store, prober: nil,
            recoverNativeScratch: { _ = try? await recovery.recover(root: URL(fileURLWithPath: "/fixture")) }, persistConcurrency: { _ in })
        let task: Task<Void, Never>?
        if action.hasPrefix("pair-") { task = Task { await session.pair(serverAddress: "localhost:8787", pin: "fixture") } }
        else { session.restore(); task = nil }
        try await waitFor { !helper.requests.isEmpty }
        #expect(transport.callCount == 0)
        if action.hasSuffix("cancel") { task?.cancel() } else { session.unpair() }
        try await waitFor { helper.stopped }
        await task?.value
        #expect(transport.callCount == 0)
        session.unpair()
    }

    @Test("a changed working folder is recovered before its capacity is advertised")
    func changedNativeWorkingFolder() async throws {
        let helper = ScratchRecoveryHelper(pages: [NativeScratchRecovery()])
        let recovery = NativeScratchRecoveryService(tools: NativeQualityCapabilityTests.tools, helper: helper)
        let settings = SettingsSnapshot()
        let proof = try #require(WorkerPerceptualCapabilities(proof: NativeQualityCapabilityTests.proof))
        let transport = ScriptedTransport([.init(status: 200, json: ["workerId": 11, "credential": "fixture", "protocolVersion": 10, "heartbeatIntervalSeconds": 30])])
        let session = SidecarSession(client: SidecarClient(transport: transport), store: InMemoryCredentialStore(),
            capabilities: SidecarCapabilities(name: "Fixture", perceptual: proof), prober: nil, settingsSnapshot: settings,
            nativeResources: { caps in
                if case let .folder(folder) = settings.workLocation { #expect(helper.roots.contains(folder.path)) }
                return caps
            }, recoverNativeScratch: { await recovery.recoverWorkingRoots(settings: settings,
                defaultRoot: URL(fileURLWithPath: "/default"), capabilityRoot: URL(fileURLWithPath: "/capability")) },
            persistConcurrency: { _ in }, sleep: { _ in try await Task.sleep(for: .milliseconds(5)) })
        await session.pair(serverAddress: "localhost:8787", pin: "fixture")
        settings.update(workLocation: .folder(URL(fileURLWithPath: "/new-choice")), memoryBudgetBytes: 0)
        try await waitFor { helper.roots.contains("/new-choice") }
        session.unpair()
        #expect(helper.roots == ["/default", "/capability", "/default", "/capability", "/new-choice"])
    }

    @Test("the panel's free space is the figure the latest check-in reported")
    func freeSpaceFollowsCheckIn() async throws {
        let store = InMemoryCredentialStore(
            stored: StoredPairing(serverAddress: "localhost:8787", credential: "c", workerId: 11))
        let transport = ScriptedTransport([
            .init(status: 200, json: ["workerId": 11, "protocolVersion": 1, "heartbeatIntervalSeconds": 30]),
        ])
        let session = SidecarSession(
            client: SidecarClient(transport: transport), store: store,
            capabilities: SidecarCapabilities(name: "Test", videoEncoders: ["libx265"], freeScratchBytes: 999, maxConcurrency: 1),
            prober: nil, executor: nil, scratchCapacity: { 4_321 }, persistConcurrency: { _ in },
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })
        #expect(session.freeScratchBytes == nil)

        session.restore()
        try await waitFor { session.freeScratchBytes != nil }

        #expect(session.freeScratchBytes == 4_321)
        session.unpair()
    }

    @Test("a running job names its encoder, and the name goes when the job does")
    func runningJobNamesItsEncoder() async throws {
        let executor = HangingExecutor()
        let session = try await workingSession(executor)

        #expect(session.jobEncoders[12] == "libx265")

        await session.stopWork(because: "test over")
        #expect(session.jobEncoders.isEmpty)
    }

    @Test("shutdown arm reports zero capacity and cancellation restores the previous pause")
    func shutdownDrainsWithoutClaiming() async throws {
        let store = InMemoryCredentialStore(
            stored: StoredPairing(serverAddress: "localhost:8787", credential: "c", workerId: 11))
        let transport = ScriptedTransport([Self.beat])
        let session = SidecarSession(
            client: SidecarClient(transport: transport), store: store,
            capabilities: SidecarCapabilities(name: "Test", videoEncoders: ["libx265"], maxConcurrency: 1),
            prober: nil, persistConcurrency: { _ in },
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) },
            requestShutdown: { Issue.record("a test must never shut down the Mac") })
        session.setPaused(true)
        await session.restoreAndSettle()
        session.armShutdown()
        try await waitFor { transport.heartbeatCapacities.contains(0) }
        #expect(transport.claims == 0)
        session.cancelShutdown()
        #expect(session.isPaused)
        try await waitFor { transport.heartbeatCapacities.last == 1 }
        session.armShutdown()
        session.unpair()
        #expect(!session.shutdown.armed)
    }

    private static let beat: ScriptedTransport.Reply =
        .init(status: 200, json: ["workerId": 11, "protocolVersion": 1, "heartbeatIntervalSeconds": 30])
    private static let claim: ScriptedTransport.Reply = .init(status: 200, json: [
        "leaseId": "lease-1", "jobId": 12, "sourceBytes": 4096, "videoEncoder": "libx265",
        "renewWithinSeconds": 30, "arguments": ["-i", "{{input}}", "{{output}}.mp4"], "outputExtension": "mp4",
        "quality": ["measure": false, "model": "vmaf_v0.6.1", "frameSubsample": 1, "clipVmaf": false,
                    "minimumHarmonicMean": 93.0, "minimumMinimum": 80.0],
    ])

    private static func claim(jobId: Int) -> ScriptedTransport.Reply {
        var json = claim.json
        json["jobId"] = jobId
        json["leaseId"] = "lease-\(jobId)"
        return .init(status: 200, json: json)
    }

    private func workingSession(
        _ executor: HangingExecutor, concurrency: Int = 1, claims: [ScriptedTransport.Reply]? = nil
    ) async throws -> SidecarSession {
        try await workingSessionAndTransport(executor, concurrency: concurrency, claims: claims).0
    }

    private func workingSessionAndTransport(
        _ executor: HangingExecutor, concurrency: Int = 1, claims: [ScriptedTransport.Reply]? = nil
    ) async throws -> (SidecarSession, ScriptedTransport) {
        let store = InMemoryCredentialStore(
            stored: StoredPairing(serverAddress: "localhost:8787", credential: "c", workerId: 11))
        // Beat, the claims, then whatever comes next (a release, a beat) is answered 204.
        let transport = ScriptedTransport([Self.beat] + (claims ?? [Self.claim]) + [.init(status: 204, json: [:])])
        let session = SidecarSession(
            client: SidecarClient(transport: transport), store: store,
            capabilities: SidecarCapabilities(name: "Test", videoEncoders: ["libx265"], maxConcurrency: concurrency),
            prober: nil,
            executor: executor,
            jobConcurrency: concurrency,
            persistConcurrency: { _ in },
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })
        session.restore()
        try await waitFor { executor.started }
        return (session, transport)
    }

    @Test("two slots take two jobs from one check-in, and no third is asked for while both are full")
    func fillsEverySlot() async throws {
        let executor = HangingExecutor()
        // Beat, two claims, then beats for ever: a third claim would be answered with a beat
        // and fail to parse, but the point is that it is never sent at all.
        let store = InMemoryCredentialStore(
            stored: StoredPairing(serverAddress: "localhost:8787", credential: "c", workerId: 11))
        let transport = ScriptedTransport([Self.beat, Self.claim(jobId: 12), Self.claim(jobId: 13), Self.beat])
        let session = SidecarSession(
            client: SidecarClient(transport: transport), store: store,
            capabilities: SidecarCapabilities(name: "Test", videoEncoders: ["libx265"], maxConcurrency: 2),
            prober: nil, executor: executor, jobConcurrency: 2, persistConcurrency: { _ in },
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })
        session.restore()

        try await waitFor { executor.startedCount == 2 }
        // Several more check-ins change nothing: both slots are full, so no claim is made.
        let claimsAfterFilling = transport.claims
        try await Task.sleep(nanoseconds: 20_000_000)
        #expect(transport.claims == claimsAfterFilling)
        #expect(executor.startedCount == 2)
        #expect(Set(session.activeJobs.keys) == [12, 13])
        guard case let .working(jobId, _) = session.status else {
            Issue.record("expected working, got \(session.status)")
            return
        }
        #expect(jobId == 12)

        await session.stopWork(because: "test over")
        #expect(session.activeJobs.isEmpty)
    }

    @Test("pausing while a claim is in flight returns its lease without starting an encode")
    func pauseDuringClaim() async throws {
        let executor = HangingExecutor()
        let transport = ScriptedTransport([Self.beat, Self.claim, .init(status: 204, json: [:])])
        let session = SidecarSession(client: SidecarClient(transport: transport),
            store: InMemoryCredentialStore(stored: StoredPairing(serverAddress: "localhost:8787", credential: "c", workerId: 11)),
            capabilities: SidecarCapabilities(name: "Test", videoEncoders: ["libx265"], maxConcurrency: 1),
            prober: nil, executor: executor, jobConcurrency: 1, persistConcurrency: { _ in },
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })
        transport.onClaim = { await session.setPaused(true) }
        session.restore()
        try await waitFor { transport.claims > 0 }
        try await waitFor { transport.releases == 1 }
        #expect(!executor.started)
        #expect(transport.releases == 1)
        await session.prepareToQuit()
    }

    @Test("shutdown stays blocked if an in-flight claim cannot be handed back")
    func shutdownDuringClaimWithFailedRelease() async throws {
        let executor = HangingExecutor()
        let transport = ScriptedTransport([Self.beat, Self.claim, Self.beat])
        transport.releaseStatusOverride = 503
        let session = SidecarSession(client: SidecarClient(transport: transport),
            store: InMemoryCredentialStore(stored: StoredPairing(serverAddress: "localhost:8787", credential: "c", workerId: 11)),
            capabilities: SidecarCapabilities(name: "Test", videoEncoders: ["libx265"], maxConcurrency: 1),
            prober: nil, executor: executor, jobConcurrency: 1, persistConcurrency: { _ in },
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) },
            requestShutdown: { Issue.record("a failed lease hand-back must never shut down the Mac") })
        transport.onClaimAfterReply = { await session.armShutdown() }
        session.restore()
        try await waitFor { transport.releases > 0 }
        try await waitFor { session.shutdown.detail.contains("not acknowledged") }
        #expect(!executor.started)
        guard case .unconfirmed = session.lastOutcome else {
            Issue.record("expected an unconfirmed hand-back")
            return
        }
        await session.prepareToQuit()
    }

    @Test("pausing keeps the active job alive and prevents additional claims")
    func pauseKeepsCurrentWork() async throws {
        let executor = HangingExecutor()
        let (session, transport) = try await workingSessionAndTransport(executor)
        session.setPaused(true)
        session.setJobConcurrency(2)
        let claims = transport.claims
        try await Task.sleep(nanoseconds: 20_000_000)
        #expect(session.isPaused)
        #expect(!executor.cancelled)
        #expect(transport.claims == claims)
        session.setPaused(false)
        #expect(!session.isPaused)
        await session.prepareToQuit()
    }

    @Test("the concurrency choice is clamped, persisted and reported on the next check-in")
    func concurrencyChoice() async throws {
        let persisted = Persisted()
        let session = SidecarSession(
            client: SidecarClient(transport: ScriptedTransport([Self.beat])),
            store: InMemoryCredentialStore(),
            capabilities: SidecarCapabilities(name: "Test", videoEncoders: ["libx265"], maxConcurrency: 1),
            prober: nil,
            jobConcurrency: 9,
            persistConcurrency: { persisted.set($0) },
            sleep: { _ in })

        #expect(session.jobConcurrency == 1)
        session.setJobConcurrency(3)
        #expect(session.jobConcurrency == 3)
        #expect(persisted.value == 3)
        session.setJobConcurrency(0)
        #expect(session.jobConcurrency == 1)
    }

    @Test("going to sleep hands the job back at once and stops checking in")
    func sleepHandsTheJobBack() async throws {
        let executor = HangingExecutor()
        let session = try await workingSession(executor)

        await session.systemWillSleep()

        #expect(executor.cancelled)
        guard case let .released(jobId, reason) = session.lastOutcome else {
            Issue.record("expected a release, got \(String(describing: session.lastOutcome))")
            return
        }
        #expect(jobId == 12)
        #expect(reason.contains("went to sleep"))
    }

    @Test("quitting hands the job back before the app exits")
    func quitHandsTheJobBack() async throws {
        let executor = HangingExecutor()
        let session = try await workingSession(executor)

        await session.prepareToQuit()

        #expect(executor.cancelled)
        guard case let .released(_, reason) = session.lastOutcome else {
            Issue.record("expected a release, got \(String(describing: session.lastOutcome))")
            return
        }
        #expect(reason.contains("quit"))
    }

    @Test("stopping work when idle is a no-op")
    func stopWhenIdle() async throws {
        let store = InMemoryCredentialStore(
            stored: StoredPairing(serverAddress: "localhost:8787", credential: "c", workerId: 11))
        let (session, _) = session([Self.beat], store: store)
        session.restore()
        try await waitFor {
            if case .connected = session.status { return true }
            return false
        }

        await session.stopWork(because: "nothing")

        #expect(session.lastOutcome == nil)
    }

}


/// A store whose read blocks the calling thread, the way the Keychain does when it is waiting on a
/// dialog. Deliberately a synchronous sleep rather than an `await`: the point of the test is that a
/// thread is held, which an `await` would not reproduce.
private final class BlockingCredentialStore: CredentialStore, @unchecked Sendable {
    private let blockFor: Duration
    private let lock = NSLock()
    private var stored: StoredPairing?

    init(blockFor: Duration, stored: StoredPairing?) {
        self.blockFor = blockFor
        self.stored = stored
    }

    func load() throws -> StoredPairing? {
        let seconds = Double(blockFor.components.seconds)
            + Double(blockFor.components.attoseconds) / 1e18
        Thread.sleep(forTimeInterval: seconds)
        return lock.withLock { stored }
    }

    func save(_ pairing: StoredPairing) throws { lock.withLock { stored = pairing } }
    func clear() throws { lock.withLock { stored = nil } }
}

@Suite("What the menu is shown")
@MainActor
struct MenuVisibilityTests {
    /// The bug this pins shipped in every build: load was sampled beside a preview frame, and the
    /// app constructed its job runner without wiring the preview gate — so no frames were ever
    /// extracted, and the GPU figure that depended on them was therefore never drawn either. One
    /// mistake, two features invisible.
    @Test("closing the menu stops frame extraction without taking the load figures with it")
    func closingTheMenuKeepsLoad() {
        let session = SidecarSession(
            client: SidecarClient(transport: ScriptedTransport([])),
            store: InMemoryCredentialStore(),
            capabilities: .provenToday(name: "Test"),
            prober: nil,
            persistConcurrency: { _ in },
            sleep: { _ in })

        session.filmStrips[1] = FilmStrip()
        session.cpu = 0.42
        session.gpu = GpuUsage(device: 0.57, memoryInUse: 0)

        session.setPreviewsWanted(false)

        // Frames cost an ffmpeg invocation each, so those stop when nobody is looking.
        #expect(session.filmStrips.isEmpty)
        // Load does not: it is cheap, and it drives the menu bar mark as well as the menu.
        #expect(session.cpu == 0.42)
        #expect(session.gpu?.device == 0.57)
    }
}
