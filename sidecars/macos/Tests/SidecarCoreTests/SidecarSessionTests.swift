import Foundation
import Testing
@testable import SidecarCore

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
    private(set) var heartbeatScratchBytes: [Int64] = []

    init(_ replies: [Reply]) {
        self.replies = replies
    }

    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let reply: Reply = lock.withLock {
            callCount += 1
            if request.url?.path.hasSuffix("/claim") == true { claims += 1 }
            if request.url?.path.hasSuffix("/heartbeat") == true,
               let body = request.httpBody,
               let json = try? JSONSerialization.jsonObject(with: body) as? [String: Any],
               let bytes = (json["freeScratchBytes"] as? NSNumber)?.int64Value {
                heartbeatScratchBytes.append(bytes)
            }
            return replies.count > 1 ? replies.removeFirst() : replies[0]
        }
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
        progress: @escaping @Sendable (JobProgress) -> Void
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
