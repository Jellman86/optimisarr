import Foundation
import Testing
@testable import SidecarCore

@Suite("Full sidecar verification")
struct FullVerificationTests {
    @Test("video contracts return decoded counts and fail closed if a count cannot be read")
    func requiredPictureEvidence() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        for candidateCount in ["398", "N/A"] {
            var contract = FullVerificationContract(version: 1, id: "fixture", measureAudio: false)
            contract.countVideoFrames = true
            let evidence = try await FullVerification(runner: TimestampFixtureRunner(candidateCount: candidateCount)).measure(
                contract: contract, ffmpeg: root, ffprobe: root, source: root.appendingPathComponent("source"),
                candidate: root.appendingPathComponent("candidate"), scratch: root,
                sourceHash: "source", candidateHash: "candidate")
            #expect(evidence.sourceDecodedFrameCount == 400)
            if candidateCount == "398" {
                #expect(evidence.candidateDecodedFrameCount == 398)
                #expect(evidence.error == nil)
            } else {
                #expect(evidence.candidateDecodedFrameCount == nil)
                #expect(evidence.error?.contains("picture counts") == true)
            }
            #expect(try FileManager.default.contentsOfDirectory(atPath: root.path).isEmpty)
        }
    }

    @Test("decoded picture counts require one positive finite primary-video count")
    func pictureCounts() {
        #expect(FullVerification.parsePictureCount("400\n") == 400)
        for bad in ["N/A", "0", "-1", "400\n390", "2147483648", "NaN", ""] {
            #expect(FullVerification.parsePictureCount(bad) == nil)
        }
        let args = FullVerification.pictureCountArguments(file: URL(fileURLWithPath: "/source file"), output: URL(fileURLWithPath: "/count"))
        #expect(args.contains("-count_frames"))
        #expect(args.contains("V:0"))
        #expect(!args.contains("-ignore_editlist"))
    }

    @Test("standalone audio measures audio packet spans without requiring picture streams")
    func standaloneAudio() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let evidence = try await FullVerification(runner: TimestampFixtureRunner(audio: true)).measure(
            contract: FullVerificationContract(version: 2, id: "fixture", measureAudio: false),
            ffmpeg: root, ffprobe: root, source: root.appendingPathComponent("source"),
            candidate: root.appendingPathComponent("candidate"), scratch: root,
            sourceHash: "source", candidateHash: "candidate")
        #expect(evidence.sourceAudio?.lastPresentationSeconds == 0.12)
        #expect(evidence.sourceVideo == nil)
        #expect(evidence.candidateVideo == nil)
        #expect(evidence.error == nil)
    }

    @Test("DTS-only source packets get presentation times without repairing candidate evidence")
    func sourceWithoutPresentationTimes() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let evidence = try await FullVerification(runner: TimestampFixtureRunner()).measure(
            contract: FullVerificationContract(version: 1, id: "fixture", measureAudio: false),
            ffmpeg: root, ffprobe: root, source: root.appendingPathComponent("source"),
            candidate: root.appendingPathComponent("candidate"), scratch: root,
            sourceHash: "source", candidateHash: "candidate")
        #expect(evidence.sourceVideo?.lastPresentationSeconds == 0.12)
        #expect(evidence.candidateVideo?.lastPresentationSeconds == 0.12)
        #expect(evidence.error == nil)
    }

    @Test("missing candidate presentation times fail with a specific reason")
    func candidateWithoutPresentationTimes() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let evidence = try await FullVerification(runner: TimestampFixtureRunner(candidateMissingPts: true)).measure(
            contract: FullVerificationContract(version: 1, id: "fixture", measureAudio: false),
            ffmpeg: root, ffprobe: root, source: root.appendingPathComponent("source"),
            candidate: root.appendingPathComponent("candidate"), scratch: root,
            sourceHash: "source", candidateHash: "candidate")
        #expect(evidence.candidateVideo?.lastPresentationSeconds == nil)
        #expect(evidence.error?.contains("candidate-video") == true)
        #expect(evidence.error?.contains("presentation") == true)
    }

    @Test("an unreconstructable source remains a failed measurement")
    func sourceWithoutAnyTimestamps() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let evidence = try await FullVerification(runner: TimestampFixtureRunner(sourceWithoutTimestamps: true)).measure(
            contract: FullVerificationContract(version: 1, id: "fixture", measureAudio: false),
            ffmpeg: root, ffprobe: root, source: root.appendingPathComponent("source"),
            candidate: root.appendingPathComponent("candidate"), scratch: root,
            sourceHash: "source", candidateHash: "candidate")
        #expect(evidence.sourceVideo?.measured == false)
        #expect(evidence.error?.contains("source-video") == true)
    }
    @Test("source and candidate timestamps select moving video, not attached artwork")
    func selectsMovingPicture() {
        #expect(FullVerification.movingPictureStreamSpecifier == "V:0")
    }

    @Test("short source picture evidence is confirmed before submission")
    func confirmsShortSourcePicture() {
        let video = VerificationTimestamps(measured: true, nonMonotonicCount: 0,
            firstRegressionDetail: nil, lastPresentationSeconds: 2900.814)
        let audio = VerificationTimestamps(measured: true, nonMonotonicCount: 0,
            firstRegressionDetail: nil, lastPresentationSeconds: 3070.25)
        let probe = #"{"streams":[{"codec_type":"video","start_time":"0"},{"codec_type":"audio","start_time":"0"}]}"#
        #expect(FullVerification.needsSourceVideoConfirmation(video: video, audio: audio, sourceProbe: probe))
        #expect(!FullVerification.needsSourceVideoConfirmation(video: audio, audio: audio, sourceProbe: probe))
        #expect(!FullVerification.needsSourceVideoConfirmation(video: video, audio: nil, sourceProbe: probe))
        let offsetProbe = #"{"streams":[{"codec_type":"video","start_time":"0","disposition":{"attached_pic":1}},{"codec_type":"video","start_time":"60","disposition":{"attached_pic":0}},{"codec_type":"audio","start_time":"0"}]}"#
        #expect(FullVerification.needsSourceVideoConfirmation(video: video, audio: video, sourceProbe: offsetProbe))
    }

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

    @Test("an earlier decode span does not extend the last presented picture")
    func earlierDecodeSpan() {
        var parser = VerificationTimestampAccumulator()
        for line in ["1371.370000,1275.899625,95.470375", "1397.020625,1396.853792,0.041708", "1396.937208,1396.937208,0.041708"] {
            parser.append(line)
        }
        #expect(parser.result.measured)
        #expect(parser.result.nonMonotonicCount == 0)
        #expect(abs(parser.result.lastPresentationSeconds! - 1397.062333) < 0.000001)
    }

    @Test("a genuinely extended final picture keeps its duration")
    func extendedFinalPicture() {
        var parser = VerificationTimestampAccumulator()
        for line in ["0,0,1", "1,1,1", "2,2,5"] { parser.append(line) }
        #expect(parser.result.lastPresentationSeconds == 7)
    }

    @Test("repeated final presentation time keeps the longest final duration")
    func repeatedFinalPicture() {
        var parser = VerificationTimestampAccumulator()
        for line in ["0,0,1", "2,1,0.01", "2,2,0.04", "1,3,9"] { parser.append(line) }
        #expect(parser.result.lastPresentationSeconds == 2.04)
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

private struct TimestampFixtureRunner: TranscodeRunner {
    var audio = false
    var candidateMissingPts = false
    var sourceWithoutTimestamps = false
    var candidateCount = "400"

    func run(_ executable: URL, _ arguments: [String], progress: @escaping @Sendable (Double) -> Void)
        async throws -> (exitCode: Int32, stderr: String) {
        guard let outputIndex = arguments.firstIndex(of: "-o") else { return (0, "") }
        let output = URL(fileURLWithPath: arguments[outputIndex + 1])
        let content: String
        if arguments.contains("-count_frames") {
            content = arguments.last?.hasSuffix("/source") == true ? "400" : candidateCount
        } else if arguments.contains("-show_streams") {
            content = #"{"streams":[{"codec_type":"video","start_time":"0"}],"format":{}}"#
        } else if arguments.contains("a:0") {
            content = audio ? "0.040000,0.000000,0.040000\n0.080000,0.040000,0.040000\n" : ""
        } else {
            let source = arguments.last?.hasSuffix("/source") == true
            // Captured shape of the VC-1 regression: decoding times exist, presentation times do not.
            let missing = source || candidateMissingPts
            content = source && sourceWithoutTimestamps ? "N/A,N/A,0.040000\n"
                : missing && !arguments.contains("+genpts")
                ? "N/A,0.000000,0.040000\nN/A,0.040000,0.040000\n"
                : "0.040000,0.000000,0.040000\n0.080000,0.040000,0.040000\n"
        }
        try content.write(to: output, atomically: true, encoding: .utf8)
        return (0, "")
    }
}
