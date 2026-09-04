import Foundation
import Testing
@testable import SidecarCore

/// Runs one real job end to end against a real Optimisarr: pair with proved capabilities, claim,
/// fetch the source, encode with the bundled ffmpeg, deliver the candidate.
///
/// Skipped unless `OPTIMISARR_LIVE_URL`, `OPTIMISARR_LIVE_PIN` and `OPTIMISARR_FFMPEG` are all
/// set, and it expects the server to have a job queued that this Mac can take. This is the
/// acceptance evidence the roadmap asks for: every stubbed test proves the loop behaves as I
/// believe the contract works, and only this proves the belief against the other implementation.
///
///     OPTIMISARR_LIVE_URL=localhost:8787 OPTIMISARR_LIVE_PIN="1234 5678" \
///     OPTIMISARR_FFMPEG=$(pwd)/vendor/ffmpeg swift test --filter LiveWorkLoop
@Suite("Live work loop", .enabled(if: ProcessInfo.processInfo.environment["OPTIMISARR_LIVE_URL"] != nil
    && ProcessInfo.processInfo.environment["OPTIMISARR_FFMPEG"] != nil))
struct LiveWorkLoopTests {
    private var serverAddress: String { ProcessInfo.processInfo.environment["OPTIMISARR_LIVE_URL"] ?? "" }
    private var pin: String { ProcessInfo.processInfo.environment["OPTIMISARR_LIVE_PIN"] ?? "" }
    private var ffmpeg: URL { URL(fileURLWithPath: ProcessInfo.processInfo.environment["OPTIMISARR_FFMPEG"] ?? "") }

    @Test("claims a real job, encodes it with the bundled ffmpeg, and delivers the candidate")
    func runsOneJob() async throws {
        let client = SidecarClient()
        let capabilities = await CapabilityProber(ffmpeg: ffmpeg).probe(name: "Live work loop", maxConcurrency: 1)
        #expect(!capabilities.videoEncoders.isEmpty, "the bundled ffmpeg proved no encoder")

        let paired = try await client.pair(serverAddress: serverAddress, pin: pin, capabilities: capabilities)
        let pairing = StoredPairing(serverAddress: serverAddress, credential: paired.credential, workerId: paired.workerId)

        // The server only offers work to a worker it has heard from.
        _ = try await client.heartbeat(
            serverAddress: serverAddress, credential: pairing.credential,
            freeScratchBytes: capabilities.freeScratchBytes, maxConcurrency: capabilities.maxConcurrency)

        let assignment = try await client.claim(serverAddress: serverAddress, credential: pairing.credential)
        guard let assignment else {
            Issue.record("the server offered no work; queue a job this Mac can take before running this")
            return
        }
        print("Assignment: job #\(assignment.jobId), encoder \(assignment.videoEncoder), output .\(assignment.outputExtension)")
        print("Command: \(assignment.arguments.joined(separator: " "))")
        #expect(capabilities.videoEncoders.contains(assignment.videoEncoder))

        let runner = JobRunner(client: client, ffmpeg: ffmpeg)
        let outcome = await runner.execute(assignment, pairing: pairing) { progress in
            print("Progress: \(progress)")
        }
        print("Outcome: \(outcome)")

        guard case let .delivered(jobId, bytes) = outcome else {
            Issue.record("expected delivery, got \(outcome)")
            return
        }
        #expect(jobId == assignment.jobId)
        #expect(bytes > 0)
    }
}
