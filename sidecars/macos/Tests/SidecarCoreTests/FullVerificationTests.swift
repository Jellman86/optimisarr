import Foundation
import Testing
@testable import SidecarCore

@Suite("Full sidecar verification")
struct FullVerificationTests {
    @Test("null-muxer timing notes and their repeats are not corrupt pictures")
    func decodeDiagnostics() {
        let notes = "Application provided invalid, non monotonically increasing dts to muxer\nLast message repeated 17 times\n"
        #expect(FullVerification.parseDecode(notes, exitCode: 0).healthy)
        let corrupt = FullVerification.parseDecode(notes + "Invalid NAL unit\nLast message repeated 3 times\n", exitCode: 0)
        #expect(!corrupt.healthy)
        #expect(corrupt.errorCount == 4)
        #expect(!FullVerification.parseDecode("", exitCode: 1).healthy)
    }

    @Test("packet reduction preserves B-frame presentation order and detects backwards DTS")
    func timestamps() {
        var parser = VerificationTimestampAccumulator()
        for line in ["0,0,0.04", "0.08,0.04,0.04", "0.04,0.08,0.04", "0.12,0.06,0.04", "N/A,N/A,N/A"] {
            parser.append(line)
        }
        #expect(parser.count == 4)
        #expect(parser.result.measured)
        #expect(parser.result.nonMonotonicCount == 1)
        #expect(parser.result.lastPresentationSeconds == 0.16)
    }

    @Test("missing and nonfinite timestamps are not successful evidence")
    func absentTimestamps() {
        var parser = VerificationTimestampAccumulator()
        parser.append("nan,inf,N/A")
        #expect(!parser.result.measured)
        #expect(parser.result.lastPresentationSeconds == nil)
    }

    @Test("loudness uses the final integrated summary, not intermediate readings")
    func loudness() {
        let result = FullVerification.parseLoudness("I: -12.0 LUFS\nI: -19.5 LUFS\nPeak: -1.2 dBFS\n", exitCode: 0)
        #expect(result.measured)
        #expect(result.integratedLufs == -19.5)
        #expect(result.truePeakDbtp == -1.2)
        #expect(!FullVerification.parseLoudness("", exitCode: 0).measured)
        #expect(!FullVerification.parseLoudness("I: -19.5 LUFS", exitCode: 1).measured)
    }

    @Test("an unavailable probe yields explicit failure evidence for the exact contract")
    func missingProbe() async throws {
        let contract = FullVerificationContract(version: 1, id: UUID().uuidString, measureAudio: true)
        let root = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent(UUID().uuidString)
        let evidence = try await FullVerification().measure(contract: contract,
            ffmpeg: root, ffprobe: nil, source: root, candidate: root, scratch: root,
            sourceHash: "source", candidateHash: "candidate")
        #expect(evidence.error != nil)
        #expect(evidence.contractId == contract.id)
        #expect(evidence.decode == nil)
    }
}
