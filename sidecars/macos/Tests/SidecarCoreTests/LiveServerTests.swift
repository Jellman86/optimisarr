import Foundation
import Testing
@testable import SidecarCore

/// Exercises this client against a real Optimisarr instance.
///
/// Skipped unless `OPTIMISARR_LIVE_URL` and `OPTIMISARR_LIVE_PIN` are set, because it needs a
/// running server and a PIN an operator has just issued — neither belongs in an unattended run.
///
/// Worth keeping despite that. The stubbed tests prove this client behaves as *I* believe the
/// contract works; only a live run proves the belief itself is right. That distinction is the
/// entire reason for writing a second implementation, and it has already caught one contract
/// mistake on the server side.
///
///     OPTIMISARR_LIVE_URL=localhost:8787 OPTIMISARR_LIVE_PIN="1234 5678" swift test
@Suite("Live server", .enabled(if: ProcessInfo.processInfo.environment["OPTIMISARR_LIVE_URL"] != nil))
struct LiveServerTests {
    private var serverAddress: String {
        ProcessInfo.processInfo.environment["OPTIMISARR_LIVE_URL"] ?? ""
    }

    private var pin: String {
        ProcessInfo.processInfo.environment["OPTIMISARR_LIVE_PIN"] ?? ""
    }

    @Test("pairs with a real server and can then check in")
    func pairsAndCheckIn() async throws {
        let client = SidecarClient()
        let capabilities = SidecarCapabilities.provenToday(name: "Live test sidecar")

        let paired = try await client.pair(
            serverAddress: serverAddress, pin: pin, capabilities: capabilities)

        #expect(paired.credential.isEmpty == false)
        #expect(paired.protocolVersion == WorkerProtocol.maximum)

        let beat = try await client.heartbeat(
            serverAddress: serverAddress,
            credential: paired.credential,
            freeScratchBytes: capabilities.freeScratchBytes,
            maxConcurrency: capabilities.maxConcurrency)

        #expect(beat.workerId == paired.workerId)
        #expect(beat.heartbeatInterval > 0)

        // The PIN is single-use, so redeeming it again must fail. Proving that against the real
        // server rather than a stub is what makes it evidence.
        await #expect(throws: SidecarError.self) {
            try await client.pair(
                serverAddress: serverAddress, pin: pin, capabilities: capabilities)
        }
    }
}
