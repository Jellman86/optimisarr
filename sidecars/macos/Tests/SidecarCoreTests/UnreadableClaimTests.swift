import Foundation
import Testing
@testable import SidecarCore

private final class UnreadableClaimTransport: HTTPTransport, @unchecked Sendable {
    let leaseId: String
    let releaseStatus: Int
    private let gate = NSLock()
    private var paths: [String] = []
    var requests: [String] { gate.withLock { paths } }

    init(leaseId: String = UUID().uuidString, releaseStatus: Int) {
        self.leaseId = leaseId; self.releaseStatus = releaseStatus
    }

    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let path = request.url!.path
        gate.withLock { paths.append(path) }
        if path.hasSuffix("/heartbeat") {
            let data = try JSONSerialization.data(withJSONObject: ["workerId": 4, "protocolVersion": 10, "heartbeatIntervalSeconds": 30])
            return (data, HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!)
        }
        let claim = path.hasSuffix("/claim")
        let data = claim ? try JSONSerialization.data(withJSONObject: ["leaseId": leaseId, "perceptualSearch": ["version": 1]]) : Data()
        return (data, HTTPURLResponse(url: request.url!, statusCode: claim ? 200 : releaseStatus, httpVersion: nil, headerFields: nil)!)
    }

    func download(_ request: URLRequest, to destination: URL) async throws -> HTTPURLResponse { try await send(request).1 }
    func upload(_ request: URLRequest, fromFile file: URL) async throws -> (Data, HTTPURLResponse) { try await send(request) }
}

@Suite("Unreadable claims")
struct UnreadableClaimTests {
    @Test("an unreadable assignment is handed back before fetching or encoding")
    func acknowledgedHandback() async {
        let transport = UnreadableClaimTransport(releaseStatus: 204)
        await #expect(throws: SidecarError.unexpectedResponse(status: 200)) {
            try await SidecarClient(transport: transport).claim(serverAddress: "http://localhost:8787", credential: "fixture")
        }
        #expect(transport.requests == ["/api/workers/claim", "/api/workers/leases/\(transport.leaseId)/release"])
    }

    @Test("a failed handback of an unreadable claim remains unconfirmed")
    func unconfirmedHandback() async {
        let transport = UnreadableClaimTransport(releaseStatus: 503)
        await #expect(throws: SidecarError.unconfirmedClaim) {
            try await SidecarClient(transport: transport).claim(serverAddress: "http://localhost:8787", credential: "fixture")
        }
        #expect(transport.requests.count == 2)
    }

    @Test("unreadable claim paths cannot be used as a release endpoint")
    func invalidIdentity() async {
        for identity in ["../../settings", "", "00000000-0000-0000-0000-000000000000"] {
            let transport = UnreadableClaimTransport(leaseId: identity, releaseStatus: 204)
            await #expect(throws: SidecarError.unconfirmedClaim) {
                try await SidecarClient(transport: transport).claim(serverAddress: "http://localhost:8787", credential: "fixture")
            }
            #expect(transport.requests == ["/api/workers/claim"])
        }
    }

    @Test("an authoritative expired lease or rejected credential also ends an unreadable claim")
    func authoritativeEnd() async {
        for status in [401, 403, 404, 409] {
            let transport = UnreadableClaimTransport(releaseStatus: status)
            await #expect(throws: SidecarError.unexpectedResponse(status: 200)) {
                try await SidecarClient(transport: transport).claim(serverAddress: "http://localhost:8787", credential: "fixture")
            }
            #expect(transport.requests.count == 2)
        }
    }
    @Test("duplicate or case-aliased lease identities cannot acknowledge the wrong job")
    func ambiguousLeaseIdentity() async {
        let first = "8b1e2c3d-0000-4000-8000-000000000001"
        let other = "8b1e2c3d-0000-4000-8000-000000000002"
        for key in ["leaseId", "LeaseId", "\\u006ceaseId"] {
            let transport = StubTransport(status: 200)
            transport.body = Data("{\"leaseId\":\"\(first)\",\"\(key)\":\"\(other)\",\"perceptualSearch\":{}}".utf8)
            await #expect(throws: SidecarError.unconfirmedClaim) {
                try await SidecarClient(transport: transport).claim(serverAddress: "http://localhost:8787", credential: "fixture")
            }
            #expect(transport.lastRequest?.url?.path == "/api/workers/claim")
        }
    }

}

@MainActor
@Suite("Unreadable claim session")
struct UnreadableClaimSessionTests {
    @Test("an unconfirmed unreadable claim blocks new claims and automatic shutdown while heartbeats continue")
    func unconfirmedClaimBlocksShutdown() async throws {
        let transport = UnreadableClaimTransport(releaseStatus: 503)
        let store = InMemoryCredentialStore()
        try store.save(StoredPairing(serverAddress: "http://localhost:8787", credential: "fixture", workerId: 4))
        let session = SidecarSession(client: SidecarClient(transport: transport), store: store,
            capabilities: SidecarCapabilities(name: "Fixture", maxConcurrency: 1), prober: nil,
            sleep: { _ in try await Task.sleep(nanoseconds: 5_000_000) },
            requestShutdown: { Issue.record("Unconfirmed claim must prevent shutdown") })
        session.restore()
        defer { session.unpair() }
        try await waitFor("check-ins after the unconfirmed handback") {
            transport.requests.filter { $0.hasSuffix("/heartbeat") }.count >= 3
        }
        #expect(transport.requests.filter { $0.hasSuffix("/claim") }.count == 1)
        session.armShutdown()
        try await waitFor("the shutdown block to be displayed") { session.shutdown.detail.contains("blocked") }
        #expect(session.shutdown.secondsRemaining(at: Date().addingTimeInterval(120)) == nil)
    }
    @Test("a pending claim is handed back when another job becomes unconfirmed before it returns")
    func unconfirmedResultDuringClaim() async throws {
        func claim(_ id: Int) -> ScriptedTransport.Reply {
            .init(status: 200, json: [
                "leaseId": UUID().uuidString, "jobId": id, "sourceBytes": 4096,
                "videoEncoder": "libx265", "renewWithinSeconds": 30,
                "arguments": ["-i", "{{input}}", "{{output}}.mp4"], "outputExtension": "mp4",
                "quality": ["measure": false, "model": "vmaf_v0.6.1", "frameSubsample": 1,
                    "clipVmaf": false, "minimumHarmonicMean": 0.0, "minimumMinimum": 0.0],
            ])
        }
        let beat = ScriptedTransport.Reply(status: 200, json: ["workerId": 4, "protocolVersion": 10, "heartbeatIntervalSeconds": 30])
        let transport = ScriptedTransport([beat, claim(1), claim(2), beat])
        transport.releaseStatusOverride = 204
        let executor = ClaimBoundaryExecutor(transport: transport)
        let store = InMemoryCredentialStore(stored: StoredPairing(serverAddress: "http://localhost:8787", credential: "fixture", workerId: 4))
        let session = SidecarSession(client: SidecarClient(transport: transport), store: store,
            capabilities: SidecarCapabilities(name: "Fixture", maxConcurrency: 2), prober: nil,
            executor: executor, jobConcurrency: 2, persistConcurrency: { _ in },
            sleep: { _ in try await Task.sleep(nanoseconds: 5_000_000) })
        transport.onClaimAfterReply = {
            if transport.claims == 2 {
                try? await waitFor("the first job's unconfirmed outcome") { session.lastOutcome != nil }
            }
        }
        session.restore()
        defer { session.unpair() }
        try await waitFor("the pending claim to be returned") { transport.releases == 1 }
        #expect(executor.startedIds == [1])
        #expect(transport.claims == 2)
    }

}

private final class ClaimBoundaryExecutor: WorkExecutor, @unchecked Sendable {
    let transport: ScriptedTransport
    private let gate = NSLock()
    private var started: [Int] = []
    var startedIds: [Int] { gate.withLock { started } }
    init(transport: ScriptedTransport) { self.transport = transport }

    func execute(_ assignment: Assignment, pairing: StoredPairing,
                 progress: @escaping @Sendable (JobProgress) -> Void,
                 preview: @escaping @Sendable (Data) -> Void,
                 storage: @escaping @Sendable (WorkStorage) -> Void) async -> JobOutcome {
        gate.withLock { started.append(assignment.jobId) }
        if assignment.jobId == 1 {
            try? await waitFor("the second claim to begin") { transport.claims == 2 }
        }
        return .unconfirmed(jobId: assignment.jobId, reason: "Fixture lost acknowledgement")
    }
}
