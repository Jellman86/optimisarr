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

    init(_ replies: [Reply]) {
        self.replies = replies
    }

    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let reply: Reply = lock.withLock {
            callCount += 1
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

    /// Polls a condition rather than sleeping a fixed time, so the tests stay fast and are not
    /// timing-sensitive on a loaded machine.
    private func waitFor(
        timeout: TimeInterval = 2,
        _ condition: @MainActor () -> Bool
    ) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if condition() { return }
            try await Task.sleep(nanoseconds: 5_000_000)
        }
        Issue.record("Condition not met within \(timeout)s")
    }
}
