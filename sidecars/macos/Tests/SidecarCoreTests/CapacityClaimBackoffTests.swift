import Foundation
import Testing
@testable import SidecarCore

private final class FixtureUptime: @unchecked Sendable {
    private let gate = NSLock()
    private var value: TimeInterval = 100
    func read() -> TimeInterval { gate.withLock { value } }
    func advance(_ seconds: TimeInterval) { gate.withLock { value += seconds } }
}

@MainActor
@Suite("Mac capacity claim backoff")
struct CapacityClaimBackoffTests {
    @Test("a confirmed capacity refusal pauses claims while normal heartbeats continue, then resumes")
    func heartbeatDuringHold() async throws {
        let clock = FixtureUptime()
        let backoff = CapacityClaimBackoff(uptime: clock.read)
        let wire = try JSONSerialization.jsonObject(with: NativeQualityAssignmentTests.wire()) as! [String: Any]
        let transport = RoutedTransport(assignment: wire)
        let store = InMemoryCredentialStore(stored: StoredPairing(serverAddress: "http://localhost:8787", credential: "fixture", workerId: 4))
        let executor = RecordingExecutor(outcome: .capacityBlocked(jobId: 12, reason: "Fixture capacity unavailable"))
        let session = SidecarSession(client: SidecarClient(transport: transport), store: store,
            capabilities: SidecarCapabilities(name: "Fixture", maxConcurrency: 1), prober: nil, executor: executor,
            capacityBackoff: backoff, sleep: { _ in try await Task.sleep(nanoseconds: 5_000_000) })
        session.restore()
        defer { session.unpair() }
        try await waitFor("check-ins during the capacity pause") { transport.heartbeats >= 3 && session.lastOutcome != nil }
        #expect(transport.claims == 1)
        #expect(backoff.isWaiting)
        clock.advance(30)
        try await waitFor("claims to resume after the monotonic pause") { transport.claims >= 2 }
        #expect(!backoff.isWaiting)
        #expect(executor.executed.count == 1)
    }
}
