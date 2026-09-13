import CryptoKit
import Foundation
import Testing
@testable import SidecarCore

// MARK: - The command contract

/// The shape the server's builder produces for a worker: software decode, placeholders for both
/// paths, the container extension on the output token.
private let serverCommand: [String] = [
    "-y", "-progress", "pipe:1", "-nostats",
    "-fflags", "+genpts",
    "-i", Assignment.inputPlaceholder,
    "-map", "0", "-map", "-0:d",
    "-map_metadata", "0",
    "-metadata", "comment=optimisarr:0.2.11",
    "-c", "copy",
    "-filter:v:0", "crop=1920:800:0:140,scale=1280:534:flags=lanczos,fps=fps=29.97",
    "-c:v:0", "hevc_videotoolbox",
    "-q:v", "60",
    "-c:a", "copy",
    "-c:s", "mov_text",
    "\(Assignment.outputPlaceholder).mp4",
]

@Suite("Assignment command contract")
struct AssignmentCommandTests {
    @Test("a command shaped like the server's is accepted and both tokens are substituted")
    func acceptsServerShape() throws {
        let command = try AssignmentCommand.validate(serverCommand, outputExtension: "mp4")

        let materialised = command.materialise(
            input: URL(fileURLWithPath: "/scratch/lease/source"),
            output: URL(fileURLWithPath: "/scratch/lease/candidate.mp4"))

        #expect(materialised[materialised.firstIndex(of: "-i")! + 1] == "/scratch/lease/source")
        #expect(materialised.last == "/scratch/lease/candidate.mp4")
        #expect(!materialised.contains { $0.contains("{{") })
    }

    @Test("an option the server's builder never emits is refused by name")
    func refusesUnknownOption() {
        var tampered = serverCommand
        tampered.insert(contentsOf: ["-passlogfile", "log"], at: 4)

        #expect(throws: AssignmentCommandError.unknownOption("-passlogfile")) {
            try AssignmentCommand.validate(tampered, outputExtension: "mp4")
        }
    }

    @Test("the input must be the placeholder, never a path on this machine")
    func refusesRealInput() {
        var tampered = serverCommand
        tampered[tampered.firstIndex(of: "-i")! + 1] = "/Users/someone/Documents/private.mov"

        #expect(throws: AssignmentCommandError.inputMustBePlaceholder("/Users/someone/Documents/private.mov")) {
            try AssignmentCommand.validate(tampered, outputExtension: "mp4")
        }
    }

    @Test("a value that looks like a path is refused even under an allowed option")
    func refusesPathLikeValue() {
        var tampered = serverCommand
        tampered[tampered.firstIndex(of: "-progress")! + 1] = "/tmp/anywhere"

        #expect(throws: AssignmentCommandError.pathLikeValue("/tmp/anywhere")) {
            try AssignmentCommand.validate(tampered, outputExtension: "mp4")
        }
    }

    @Test("the output token must be last and carry the promised extension")
    func refusesOutputDrift() {
        #expect(throws: AssignmentCommandError.outputMustBeLast(expected: "{{output}}.mkv")) {
            try AssignmentCommand.validate(serverCommand, outputExtension: "mkv")
        }

        var reordered = serverCommand
        reordered.append("-y")
        #expect(throws: AssignmentCommandError.outputMustBeLast(expected: "{{output}}.mp4")) {
            try AssignmentCommand.validate(reordered, outputExtension: "mp4")
        }
    }

    @Test("a filtergraph's escaped comma is not mistaken for a Windows path")
    func acceptsFiltergraphEscapes() throws {
        // The frame-rate cap's select filter escapes its comma with a backslash. The first real
        // capped encode was refused for it; a backslash before a path segment is still refused.
        var capped = serverCommand
        capped[capped.firstIndex(of: "-filter:v:0")! + 1] = #"crop=1920:800:0:140,select=not(mod(round(t*60)\,2))"#
        _ = try AssignmentCommand.validate(capped, outputExtension: "mp4")

        var windows = serverCommand
        windows[windows.firstIndex(of: "-progress")! + 1] = #"C:\Users\someone\progress.txt"#
        #expect(throws: AssignmentCommandError.pathLikeValue(#"C:\Users\someone\progress.txt"#)) {
            try AssignmentCommand.validate(windows, outputExtension: "mp4")
        }
    }

    @Test("this platform's own hardware decoder is accepted, any other is refused by name")
    func hardwareDecoder() throws {
        var apple = serverCommand
        apple.insert(contentsOf: ["-hwaccel", "videotoolbox"], at: 0)
        let command = try AssignmentCommand.validate(apple, outputExtension: "mp4")
        #expect(command.arguments.prefix(2) == ["-hwaccel", "videotoolbox"])

        var other = serverCommand
        other.insert(contentsOf: ["-hwaccel", "cuda"], at: 0)
        #expect(throws: AssignmentCommandError.unknownHardwareDecoder("cuda")) {
            try AssignmentCommand.validate(other, outputExtension: "mp4")
        }
    }

    @Test("a second input is refused however it is spelt")
    func refusesSecondInput() {
        var tampered = serverCommand
        tampered.insert(contentsOf: ["-i", Assignment.inputPlaceholder], at: 4)

        #expect(throws: AssignmentCommandError.exactlyOneInputRequired(2)) {
            try AssignmentCommand.validate(tampered, outputExtension: "mp4")
        }
    }

    @Test("progress lines yield encoded seconds and nothing else does")
    func progressLines() {
        #expect(FfmpegProgressLine.encodedSeconds("out_time_us=1500000") == 1.5)
        #expect(FfmpegProgressLine.encodedSeconds("out_time_ms=1500000") == 1.5)
        #expect(FfmpegProgressLine.encodedSeconds("frame=42") == nil)
        #expect(FfmpegProgressLine.encodedSeconds("out_time_us=-1") == nil)
    }
}

// MARK: - A stand-in server

/// Answers the worker routes the way Optimisarr does, and records what it was sent, so the whole
/// claim-fetch-encode-deliver flow can be walked without a server or an ffmpeg.
final class FakeWorkerServer: HTTPTransport, @unchecked Sendable {
    let sourceBytes: Data
    var renewStatus = 200
    var deliverStatus = 202
    var claimJSON: [String: Any]?

    private let lock = NSLock()
    private(set) var deliveredFile: Data?
    private(set) var deliveredHeaders: [String: String] = [:]
    private(set) var released = false
    private(set) var renewals = 0
    private(set) var qualityReport: [String: Any]?
    private(set) var qualityReportedBeforeDelivery = false
    /// Whether the server offers resumable delivery; off means an older server, whole-file only.
    var resumable = false
    /// Drop the chunk that starts at this offset once, as a failed connection would.
    var dropChunkAt: Int64? = nil
    private var staged = Data()
    private(set) var chunkOffsets: [Int64] = []
    private(set) var completedViaChunks = false
    /// Serve the source in the byte ranges the client asks for rather than as one response.
    var rangedSource = false
    /// Drop the range beginning at this offset once, as an interrupted download would.
    var dropSourceAt: Int64? = nil
    private(set) var sourceOffsets: [Int64] = []
    private(set) var sourceDownloads = 0
    var sourceDelay: TimeInterval = 0
    private(set) var sourceDownloadCompleted = false
    var deliveryDelay: TimeInterval = 0
    private(set) var deliveryCompleted = false

    init(sourceBytes: Data, claimJSON: [String: Any]? = nil) {
        self.sourceBytes = sourceBytes
        self.claimJSON = claimJSON
    }

    var sourceSha256: String {
        SHA256.hash(data: sourceBytes).map { String(format: "%02x", $0) }.joined()
    }

    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let path = request.url!.path
        return try lock.withLock {
            if path.hasSuffix("/claim") {
                guard let claimJSON else { return (Data(), response(request, 204)) }
                return (try JSONSerialization.data(withJSONObject: claimJSON), response(request, 200))
            }
            if path.hasSuffix("/renew") {
                renewals += 1
                let body = renewStatus == 200 ? Data() : Data(#"{"error":"That lease has expired."}"#.utf8)
                return (body, response(request, renewStatus))
            }
            if path.hasSuffix("/release") {
                released = true
                return (Data(), response(request, 204))
            }
            if path.hasSuffix("/result/offset") {
                guard resumable else { return (Data(), response(request, 404)) }
                return (Data("{\"bytes\":\(staged.count)}".utf8), response(request, 200))
            }
            if path.hasSuffix("/result"), request.httpMethod == "PATCH" {
                guard resumable else { return (Data(), response(request, 404)) }
                let offset = Int64(request.value(forHTTPHeaderField: "X-Optimisarr-Offset") ?? "-1") ?? -1
                chunkOffsets.append(offset)
                if let drop = dropChunkAt, drop == offset {
                    dropChunkAt = nil
                    throw SidecarError.transferFailed(reason: "connection reset")
                }
                guard offset == Int64(staged.count) else {
                    let body = Data("{\"error\":\"offset\",\"details\":{\"bytes\":\(staged.count)}}".utf8)
                    return (body, response(request, 409))
                }
                staged.append(request.httpBody ?? Data())
                return (Data("{\"bytes\":\(staged.count)}".utf8), response(request, 200))
            }
            if path.hasSuffix("/result/complete") {
                guard resumable else { return (Data(), response(request, 404)) }
                deliveredFile = staged
                deliveredHeaders = request.allHTTPHeaderFields ?? [:]
                completedViaChunks = true
                let body = deliverStatus == 202
                    ? Data(#"{"jobId":12,"bytes":\#(staged.count),"candidateSha256":"x"}"#.utf8)
                    : Data(#"{"error":"The uploaded candidate does not match the hash the worker declared."}"#.utf8)
                return (body, response(request, deliverStatus))
            }
            if path.hasSuffix("/quality") {
                qualityReport = try JSONSerialization.jsonObject(with: request.httpBody ?? Data()) as? [String: Any]
                qualityReportedBeforeDelivery = deliveredFile == nil
                return (Data(#"{"leaseId":"x","vmafHarmonicMean":94.9}"#.utf8), response(request, 200))
            }
            return (Data(), response(request, 404))
        }
    }

    func download(_ request: URLRequest, to destination: URL) async throws -> HTTPURLResponse {
        sourceDownloads += 1
        if sourceDelay > 0 {
            try await Task.sleep(nanoseconds: UInt64(sourceDelay * 1_000_000_000))
        }
        sourceDownloadCompleted = true
        if rangedSource {
            let range = request.value(forHTTPHeaderField: "Range") ?? ""
            let bounds = range
                .replacingOccurrences(of: "bytes=", with: "")
                .split(separator: "-", maxSplits: 1)
            let start = Int64(bounds.first ?? "") ?? -1
            let requestedEnd = Int64(bounds.count > 1 ? bounds[1] : "") ?? -1
            sourceOffsets.append(start)
            if let drop = dropSourceAt, drop == start {
                dropSourceAt = nil
                throw SidecarError.transferFailed(reason: "connection reset")
            }
            let end = min(requestedEnd, Int64(sourceBytes.count) - 1)
            let chunk = sourceBytes[Int(start)...Int(end)]
            if !FileManager.default.fileExists(atPath: destination.path) {
                FileManager.default.createFile(atPath: destination.path, contents: nil)
            }
            let handle = try FileHandle(forWritingTo: destination)
            defer { try? handle.close() }
            try handle.seekToEnd()
            try handle.write(contentsOf: chunk)
            return response(request, 206, headers: [
                "Content-Range": "bytes \(start)-\(end)/\(sourceBytes.count)",
                "X-Optimisarr-Source-Sha256": sourceSha256,
            ])
        }
        try sourceBytes.write(to: destination)
        return response(request, 200, headers: ["X-Optimisarr-Source-Sha256": sourceSha256])
    }

    func upload(_ request: URLRequest, fromFile file: URL) async throws -> (Data, HTTPURLResponse) {
        if deliveryDelay > 0 {
            try await Task.sleep(nanoseconds: UInt64(deliveryDelay * 1_000_000_000))
        }
        let delivered = try Data(contentsOf: file)
        return lock.withLock {
            deliveryCompleted = true
            deliveredFile = delivered
            deliveredHeaders = request.allHTTPHeaderFields ?? [:]
            let body = deliverStatus == 202
                ? Data(#"{"jobId":12,"bytes":\#(delivered.count),"candidateSha256":"x"}"#.utf8)
                : Data(#"{"error":"The uploaded candidate does not match the hash the worker declared."}"#.utf8)
            return (body, response(request, deliverStatus))
        }
    }

    private func response(_ request: URLRequest, _ status: Int, headers: [String: String] = [:]) -> HTTPURLResponse {
        HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: nil, headerFields: headers)!
    }
}

@Suite("Resumable source download")
struct ResumableSourceDownloadTests {
    @Test("a dropped source range resumes from the last complete byte without downloading it twice")
    func resumesAfterDroppedRange() async throws {
        let bytes = Data((0..<200).map(UInt8.init))
        let server = FakeWorkerServer(sourceBytes: bytes)
        server.rangedSource = true
        server.dropSourceAt = 64
        let runner = JobRunner(
            client: SidecarClient(transport: server, downloadChunkBytes: 64),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(),
            scratchRoot: scratch(),
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        let outcome = await runner.execute(assignment(), pairing: pairing) { _ in }

        #expect(outcome == .delivered(jobId: 12, bytes: 15))
        #expect(server.sourceOffsets == [0, 64, 64, 128, 192])
        #expect(server.deliveredFile == Data("candidate bytes".utf8))
    }
}

/// Stands in for ffmpeg: writes the candidate the command names, or fails, without encoding.
/// Remembers what a fake was asked to run, so a test can look at the materialised command.
final class ArgumentRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var recorded: [[String]] = []
    var all: [[String]] { lock.withLock { recorded } }
    func record(_ arguments: [String]) { lock.withLock { recorded.append(arguments) } }
}

/// Stands in for ffprobe: answers a container and video start for whichever file is named last.
struct FakeLeadProbe: CommandRunner {
    var candidate = (container: "0.000000", video: "0.041000")
    var source = (container: "-0.021000", video: "0.000000")
    var exitCode: Int32 = 0

    func run(_ executable: URL, _ arguments: [String]) async -> (exitCode: Int32, output: String) {
        let starts = arguments.last!.hasPrefix("/") && arguments.last!.contains("candidate") ? candidate : source
        return (exitCode, """
        {"streams":[{"codec_type":"video","start_time":"\(starts.video)"},{"codec_type":"audio","start_time":"-0.021000"}],
         "format":{"start_time":"\(starts.container)"}}
        """)
    }
}

struct FakeTranscodeRunner: TranscodeRunner, @unchecked Sendable {
    var exitCode: Int32 = 0
    var candidate = Data("candidate bytes".utf8)
    var delay: TimeInterval = 0
    var recorder: ArgumentRecorder?

    var measurementExitCode: Int32 = 0

    func run(
        _ executable: URL, _ arguments: [String], progress: @escaping @Sendable (Double) -> Void
    ) async throws -> (exitCode: Int32, stderr: String) {
        if delay > 0 { try await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000)) }
        recorder?.record(arguments)
        // A measurement names its log inside the filter; write the kind of log libvmaf would.
        // A token left in the filter is what real ffmpeg would choke on, so this fake does too.
        if let filter = arguments.firstIndex(of: "-lavfi").map({ arguments[$0 + 1] }),
           let range = filter.range(of: "log_path=") {
            if filter.contains("{{") { return (1, "Invalid argument") }
            let logPath = String(filter[range.upperBound...]).components(separatedBy: ":")[0]
            if measurementExitCode == 0 {
                try Data(#"{"frames":[{"frameNum":0,"metrics":{"vmaf":96.0}}],"pooled_metrics":{"vmaf":{"min":96.0,"max":96.0,"mean":96.0,"harmonic_mean":96.0}}}"#.utf8)
                    .write(to: URL(fileURLWithPath: logPath))
            }
            return (measurementExitCode, "")
        }
        progress(12.5)
        if exitCode == 0 {
            try candidate.write(to: URL(fileURLWithPath: arguments.last!))
        }
        return (exitCode, exitCode == 0 ? "" : "Error while opening encoder")
    }
}

/// A sampled-window measurement as the server now builds it: seeked onto the source's frame grid,
/// with the candidate's extra lead left for the worker to measure and fill in.
private let shiftedMeasurementCommand: [String] = [
    "-nostdin", "-v", "error", "-stats",
    "-ss", "113.008875", "-i", "{{distorted}}", "-ss", "113.008875", "-i", "{{reference}}",
    "-lavfi", "[0:v]settb=AVTB,setpts=PTS-{{distortedShift}}*1000000,fps=fps=23.976023976023978:start_time=0,trim=start=4.991125:duration=40,settb=AVTB,setpts=PTS-STARTPTS,scale=1920:1080:flags=bicubic:in_range=auto:out_range=tv,format=yuv420p[dist];[1:v]settb=AVTB,fps=fps=23.976023976023978:start_time=0,trim=start=4.991125:duration=40,settb=AVTB,setpts=PTS-STARTPTS,scale=1920:1080:flags=bicubic:in_range=auto:out_range=tv,format=yuv420p[ref];[dist][ref]libvmaf=model=version=vmaf_v0.6.1:n_threads=8:n_subsample=1:log_fmt=json:log_path={{log}}:shortest=1:repeatlast=0",
    "-t", "40",
    "-f", "null", "-",
]

private let measurementCommand: [String] = [
    "-nostdin", "-v", "error", "-stats",
    "-i", "{{distorted}}", "-i", "{{reference}}",
    "-lavfi", "[0:v]scale=1920:1080:flags=bicubic:in_range=auto:out_range=tv,format=yuv420p[dist];[1:v]scale=1920:1080:flags=bicubic:in_range=auto:out_range=tv,format=yuv420p[ref];[dist][ref]libvmaf=model=version=vmaf_v0.6.1:n_threads=8:n_subsample=1:log_fmt=json:log_path={{log}}:shortest=1:repeatlast=0",
    "-f", "null", "-",
]

private func assignment(
    renewWithinSeconds: Int = 30, measure: Bool = false, sourceBytes: Int64 = 4_096,
    commands: [[String]] = [measurementCommand]
) -> Assignment {
    Assignment(
        leaseId: "8b1e2c3d-0000-4000-8000-000000000001", jobId: 12, sourceBytes: sourceBytes,
        videoEncoder: "hevc_videotoolbox", renewWithinSeconds: renewWithinSeconds,
        arguments: serverCommand, outputExtension: "mp4",
        quality: QualityRequirement(
            measure: measure, model: "vmaf_v0.6.1", frameSubsample: 1, clipVmaf: false,
            minimumHarmonicMean: 93, minimumMinimum: 80,
            commands: measure ? commands : [], sampling: "Full file"))
}

@Suite("Scratch capacity")
struct ScratchCapacityTests {
    @Test("a job that no longer fits is handed back before its source is downloaded")
    func refusesBeforeDownload() async throws {
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 7, count: 100))
        let runner = JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(),
            scratchRoot: scratch(),
            availableScratchBytes: { _ in 149 },
            sleep: { _ in })

        let outcome = await runner.execute(
            assignment(sourceBytes: 100), pairing: pairing) { _ in }

        guard case let .released(jobId, reason) = outcome else {
            Issue.record("expected a release, got \(outcome)")
            return
        }
        #expect(jobId == 12)
        #expect(reason.contains("150 bytes"))
        #expect(reason.contains("149 bytes"))
        #expect(server.sourceDownloads == 0)
        #expect(server.deliveredFile == nil)
    }
}

private let pairing = StoredPairing(serverAddress: "localhost:8787", credential: "secret", workerId: 3)

private func scratch() -> URL {
    FileManager.default.temporaryDirectory.appendingPathComponent("optimisarr-worktest-\(UUID().uuidString)")
}

@Suite("Job runner")
struct JobRunnerTests {
    @Test("a healthy job fetches, encodes, hashes and delivers, then leaves no scratch behind")
    func deliversAndCleansUp() async throws {
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 7, count: 4_096))
        let root = scratch()
        let runner = JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(),
            scratchRoot: root,
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        let outcome = await runner.execute(assignment(), pairing: pairing) { _ in }

        #expect(outcome == .delivered(jobId: 12, bytes: 15))
        #expect(server.deliveredFile == Data("candidate bytes".utf8))
        // Both hashes travel with the candidate, so the server can bind it to this exact source.
        #expect(server.deliveredHeaders["X-Optimisarr-Source-Sha256"] == server.sourceSha256)
        let expectedCandidateHash = SHA256.hash(data: Data("candidate bytes".utf8))
            .map { String(format: "%02x", $0) }.joined()
        #expect(server.deliveredHeaders["X-Optimisarr-Candidate-Sha256"] == expectedCandidateHash)
        #expect(server.released == false)
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("lease-\(assignment().leaseId)").path))
    }

    @Test("a source that arrives corrupt is never encoded and the job is handed back")
    func corruptSourceIsReleased() async throws {
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 7, count: 4_096))
        let lying = LyingHashServer(inner: server)
        let runner = JobRunner(
            client: SidecarClient(transport: lying),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(),
            scratchRoot: scratch(),
            sleep: { _ in })

        let outcome = await runner.execute(assignment(), pairing: pairing) { _ in }

        guard case let .released(jobId, reason) = outcome else {
            Issue.record("expected a release, got \(outcome)")
            return
        }
        #expect(jobId == 12)
        #expect(reason.contains("hash mismatch"))
        #expect(server.released)
        #expect(server.deliveredFile == nil)
    }

    @Test("a failed encode hands the job back with ffmpeg's reason")
    func failedEncodeIsReleased() async throws {
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 1, count: 64))
        let runner = JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(exitCode: 1),
            scratchRoot: scratch(),
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        let outcome = await runner.execute(assignment(), pairing: pairing) { _ in }

        guard case let .released(_, reason) = outcome else {
            Issue.record("expected a release, got \(outcome)")
            return
        }
        #expect(reason.contains("Error while opening encoder"))
        #expect(server.released)
        #expect(server.deliveredFile == nil)
    }

    @Test("a command the contract refuses is handed back before any byte is fetched")
    func refusedCommandIsReleased() async throws {
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 1, count: 64))
        var tampered = assignment()
        tampered = Assignment(
            leaseId: tampered.leaseId, jobId: tampered.jobId, sourceBytes: tampered.sourceBytes,
            videoEncoder: tampered.videoEncoder, renewWithinSeconds: tampered.renewWithinSeconds,
            arguments: ["-i", "/etc/passwd", "{{output}}.mp4"], outputExtension: "mp4",
            quality: tampered.quality)
        let runner = JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(),
            scratchRoot: scratch(),
            sleep: { _ in })

        let outcome = await runner.execute(tampered, pairing: pairing) { _ in }

        guard case let .released(_, reason) = outcome else {
            Issue.record("expected a release, got \(outcome)")
            return
        }
        #expect(reason.contains("refused"))
        #expect(server.released)
    }

    @Test("losing the lease mid-encode stops the work rather than finishing it for nobody")
    func lostLeaseCancelsEncode() async throws {
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 1, count: 64))
        server.renewStatus = 409
        let runner = JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            // Long enough that the renewal, which fires almost at once, wins the race.
            runner: FakeTranscodeRunner(delay: 5),
            scratchRoot: scratch(),
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        let started = Date()
        let outcome = await runner.execute(assignment(renewWithinSeconds: 10), pairing: pairing) { _ in }

        guard case .leaseLost = outcome else {
            Issue.record("expected the lease to be lost, got \(outcome)")
            return
        }
        #expect(Date().timeIntervalSince(started) < 4)
        #expect(server.deliveredFile == nil)
    }

    @Test("losing the lease during a source transfer cancels the download")
    func lostLeaseCancelsSourceTransfer() async throws {
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 1, count: 64))
        server.sourceDelay = 0.2
        server.renewStatus = 409
        let runner = JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(),
            scratchRoot: scratch(),
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        let outcome = await runner.execute(
            assignment(renewWithinSeconds: 10), pairing: pairing) { _ in }

        guard case .leaseLost = outcome else {
            Issue.record("expected the lease to be lost, got \(outcome)")
            return
        }
        #expect(!server.sourceDownloadCompleted)
        #expect(server.deliveredFile == nil)
    }

    @Test("losing the lease during delivery cancels the upload")
    func lostLeaseCancelsDelivery() async throws {
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 1, count: 64))
        server.deliveryDelay = 0.2
        server.renewStatus = 409
        let runner = JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(),
            scratchRoot: scratch(),
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        let outcome = await runner.execute(
            assignment(renewWithinSeconds: 10), pairing: pairing) { _ in }

        guard case .leaseLost = outcome else {
            Issue.record("expected the lease to be lost, got \(outcome)")
            return
        }
        #expect(!server.deliveryCompleted)
        #expect(server.deliveredFile == nil)
    }
}

/// Wraps a server so the declared source hash is wrong, standing in for a transfer that was
/// truncated or corrupted on the way.
final class LyingHashServer: HTTPTransport, @unchecked Sendable {
    let inner: FakeWorkerServer
    init(inner: FakeWorkerServer) { self.inner = inner }

    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) { try await inner.send(request) }

    func download(_ request: URLRequest, to destination: URL) async throws -> HTTPURLResponse {
        _ = try await inner.download(request, to: destination)
        return HTTPURLResponse(
            url: request.url!, statusCode: 200, httpVersion: nil,
            headerFields: ["X-Optimisarr-Source-Sha256": String(repeating: "0", count: 64)])!
    }

    func upload(_ request: URLRequest, fromFile file: URL) async throws -> (Data, HTTPURLResponse) {
        try await inner.upload(request, fromFile: file)
    }
}

// MARK: - The session's loop

/// Records the assignment it was handed and answers with a fixed outcome.
final class RecordingExecutor: WorkExecutor, @unchecked Sendable {
    private(set) var executed: [Assignment] = []
    let outcome: JobOutcome
    init(outcome: JobOutcome) { self.outcome = outcome }

    func execute(
        _ assignment: Assignment, pairing: StoredPairing,
        progress: @escaping @Sendable (JobProgress) -> Void
    ) async -> JobOutcome {
        executed.append(assignment)
        progress(.encoding(encodedSeconds: 3))
        return outcome
    }
}

/// Answers heartbeats forever and hands out one assignment on the first claim, so a session can be
/// walked through pick-up, execution and return to idle without a scripted reply running out.
final class RoutedTransport: HTTPTransport, @unchecked Sendable {
    private let lock = NSLock()
    private var assignment: [String: Any]?
    private(set) var claims = 0
    private(set) var heartbeats = 0

    init(assignment: [String: Any]?) {
        self.assignment = assignment
    }

    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let path = request.url!.path
        return try lock.withLock {
            if path.hasSuffix("/heartbeat") {
                heartbeats += 1
                let body = try JSONSerialization.data(withJSONObject: [
                    "workerId": 4, "protocolVersion": 1, "heartbeatIntervalSeconds": 30,
                ])
                return (body, HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!)
            }
            if path.hasSuffix("/claim") {
                claims += 1
                guard let assignment else {
                    return (Data(), HTTPURLResponse(url: request.url!, statusCode: 204, httpVersion: nil, headerFields: nil)!)
                }
                self.assignment = nil
                return (try JSONSerialization.data(withJSONObject: assignment),
                        HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!)
            }
            return (Data(), HTTPURLResponse(url: request.url!, statusCode: 404, httpVersion: nil, headerFields: nil)!)
        }
    }

    func download(_ request: URLRequest, to destination: URL) async throws -> HTTPURLResponse {
        HTTPURLResponse(url: request.url!, statusCode: 404, httpVersion: nil, headerFields: nil)!
    }

    func upload(_ request: URLRequest, fromFile file: URL) async throws -> (Data, HTTPURLResponse) {
        try await send(request)
    }
}

@MainActor
@Suite("Session work loop")
struct SessionWorkLoopTests {
    private func assignmentJSON() -> [String: Any] {
        [
            "leaseId": "8b1e2c3d-0000-4000-8000-000000000001", "jobId": 12, "sourceBytes": 4_096,
            "videoEncoder": "hevc_videotoolbox", "vmaf": "Cpu",
            "expiresUtc": "2026-09-04T10:00:00+00:00", "renewWithinSeconds": 30,
            "arguments": serverCommand, "outputExtension": "mp4",
            "quality": [
                "measure": true, "model": "vmaf_v0.6.1", "frameSubsample": 1, "clipVmaf": false,
                "minimumHarmonicMean": 93.0, "minimumMinimum": 80.0,
            ],
        ]
    }

    @Test("a claim that returns work runs it and the session reports the outcome")
    func claimsAndRuns() async throws {
        let executor = RecordingExecutor(outcome: .delivered(jobId: 12, bytes: 15))
        let store = InMemoryCredentialStore()
        try store.save(StoredPairing(serverAddress: "localhost:8787", credential: "c", workerId: 4))
        let transport = RoutedTransport(assignment: assignmentJSON())
        let session = SidecarSession(
            client: SidecarClient(transport: transport),
            store: store,
            capabilities: SidecarCapabilities(name: "Test", videoEncoders: ["hevc_videotoolbox"], vmaf: .cpu, maxConcurrency: 1),
            prober: nil,
            executor: executor,
            sleep: { _ in try await Task.sleep(nanoseconds: 5_000_000) })

        session.restore()
        // Poll rather than sleep: the loop's own tasks decide when this is true, and how long
        // that takes depends on the machine.
        try await waitFor("the job to be executed and the loop to ask again") {
            !executor.executed.isEmpty && session.lastOutcome != nil && transport.claims >= 2
        }

        #expect(executor.executed.map(\.jobId) == [12])
        #expect(executor.executed.first?.arguments == serverCommand)
        #expect(session.lastOutcome == .delivered(jobId: 12, bytes: 15))
        if case .connected = session.status {} else {
            Issue.record("expected the session back to connected, got \(session.status)")
        }
        // Idle again, so the loop keeps asking; the server simply has nothing more.
        #expect(transport.claims >= 2)
        session.unpair()
    }

    @Test("a worker advertising no slot never claims")
    func drainedWorkerNeverClaims() async throws {
        let executor = RecordingExecutor(outcome: .delivered(jobId: 12, bytes: 15))
        let store = InMemoryCredentialStore()
        try store.save(StoredPairing(serverAddress: "localhost:8787", credential: "c", workerId: 4))
        let transport = RoutedTransport(assignment: assignmentJSON())
        let session = SidecarSession(
            client: SidecarClient(transport: transport),
            store: store,
            capabilities: .provenToday(name: "Test"),
            prober: nil,
            executor: executor,
            sleep: { _ in try await Task.sleep(nanoseconds: 5_000_000) })

        session.restore()
        // Wait for the loop to be demonstrably running before asserting what it did not do;
        // otherwise this passes simply by looking too early.
        try await waitFor("the check-in loop to run") { transport.heartbeats > 0 }
        try await Task.sleep(nanoseconds: 50_000_000)

        #expect(executor.executed.isEmpty)
        // Only heartbeats went out: a drained worker asking for work would be a wasted request.
        #expect(transport.heartbeats > 0)
        #expect(transport.claims == 0)
        session.unpair()
    }
}

@Suite("Measurement command")
struct MeasurementCommandTests {
    @Test("the server's libvmaf command is accepted and all three tokens are substituted")
    func acceptsServerShape() throws {
        let command = try MeasurementCommand.validate(measurementCommand)
        let materialised = command.materialise(
            distorted: URL(fileURLWithPath: "/scratch/candidate.mp4"),
            reference: URL(fileURLWithPath: "/scratch/source"),
            log: URL(fileURLWithPath: "/scratch/vmaf-0.json"))

        #expect(materialised[5] == "/scratch/candidate.mp4")
        #expect(materialised[7] == "/scratch/source")
        #expect(materialised[9].contains("log_path=/scratch/vmaf-0.json"))
        #expect(!materialised.joined(separator: " ").contains("{{"))
    }

    @Test("inputs must be the two tokens in libvmaf's order, never a path")
    func refusesRealInputs() {
        var swapped = measurementCommand
        swapped[5] = "{{reference}}"; swapped[7] = "{{distorted}}"
        #expect(throws: MeasurementCommandError.inputsMustBePlaceholders(["{{reference}}", "{{distorted}}"])) {
            try MeasurementCommand.validate(swapped)
        }
        var real = measurementCommand
        real[7] = "/Volumes/Media/film.mkv"
        #expect(throws: MeasurementCommandError.inputsMustBePlaceholders(["{{distorted}}", "/Volumes/Media/film.mkv"])) {
            try MeasurementCommand.validate(real)
        }
    }

    @Test("a command that writes anything but a log is refused")
    func refusesRealOutput() {
        var writes = measurementCommand
        writes[writes.count - 2] = "mp4"; writes[writes.count - 1] = "out.mp4"
        #expect(throws: MeasurementCommandError.mustEndWithNullOutput) { try MeasurementCommand.validate(writes) }
    }

    @Test("an option the server's builder never emits is refused by name")
    func refusesUnknownOption() {
        var extra = measurementCommand
        extra.insert(contentsOf: ["-hwaccel", "videotoolbox"], at: 4)
        #expect(throws: MeasurementCommandError.unknownOption("-hwaccel")) { try MeasurementCommand.validate(extra) }
    }

    @Test("the shift token is allowed inside the filter only, and is filled with the measured lead")
    func shiftToken() throws {
        let command = try MeasurementCommand.validate(shiftedMeasurementCommand)
        #expect(command.needsDistortedShift)
        #expect(!(try MeasurementCommand.validate(measurementCommand)).needsDistortedShift)

        let materialised = command.materialise(
            distorted: URL(fileURLWithPath: "/tmp/c.mp4"), reference: URL(fileURLWithPath: "/tmp/s.mkv"),
            log: URL(fileURLWithPath: "/tmp/v.json"), distortedShift: "0.02")
        #expect(materialised[13].contains("setpts=PTS-0.02*1000000,fps="))
        #expect(!materialised[13].contains("{{"))

        var stray = shiftedMeasurementCommand
        stray[5] = "{{distortedShift}}"
        #expect(throws: MeasurementCommandError.strayPlaceholder("{{distortedShift}}")) {
            try MeasurementCommand.validate(stray)
        }
    }

    @Test("a picture lead is the video start less the container start, and the shift is their difference")
    func leadAndShift() {
        let json = """
        {"streams":[{"codec_type":"audio","start_time":"-0.021000"},{"codec_type":"video","start_time":"0.000000"}],
         "format":{"start_time":"-0.021000"}}
        """
        #expect(TimelineLead.parse(json) == 0.021)
        #expect(TimelineLead.parse(#"{"streams":[],"format":{}}"#) == nil)
        #expect(TimelineLead.shift(candidate: 0.041, source: 0.021) == "0.02")
        #expect(TimelineLead.shift(candidate: 0.0, source: 0.021) == "-0.021")
        #expect(TimelineLead.shift(candidate: 0.5, source: 0.5) == "0")
    }

    @Test("the filter must name the log token exactly once and nothing else may")
    func requiresOneLogToken() {
        var none = measurementCommand
        none[9] = none[9].replacingOccurrences(of: "{{log}}", with: "vmaf.json")
        #expect(throws: MeasurementCommandError.logPlaceholderMissing) { try MeasurementCommand.validate(none) }
        var stray = measurementCommand
        stray[2] = "{{log}}"
        #expect(throws: MeasurementCommandError.strayPlaceholder("{{log}}")) { try MeasurementCommand.validate(stray) }
    }
}

@Suite("Measurement in the job flow")
struct MeasurementFlowTests {
    @Test("a gated job measures after encoding and reports the logs, bound to both hashes, before delivering")
    func reportsBeforeDelivery() async throws {
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 7, count: 4_096))
        let runner = JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(),
            scratchRoot: scratch(),
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        let outcome = await runner.execute(assignment(measure: true), pairing: pairing) { _ in }

        #expect(outcome == .delivered(jobId: 12, bytes: 15))
        let report = try #require(server.qualityReport)
        #expect(server.qualityReportedBeforeDelivery)
        #expect(report["sourceSha256"] as? String == server.sourceSha256)
        #expect(report["candidateSha256"] as? String == server.deliveredHeaders["X-Optimisarr-Candidate-Sha256"])
        let logs = try #require(report["logs"] as? [String])
        #expect(logs.count == 1)
        #expect(logs[0].contains("harmonic_mean"))
    }

    @Test("a sampled measurement fills in the candidate's lead from both files before running")
    func fillsInTheShift() async throws {
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 7, count: 4_096))
        let recorder = ArgumentRecorder()
        var fake = FakeTranscodeRunner()
        fake.recorder = recorder
        let runner = JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            ffprobe: URL(fileURLWithPath: "/usr/bin/true"),
            runner: fake,
            leadProbe: FakeLeadProbe(),
            scratchRoot: scratch(),
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        let outcome = await runner.execute(
            assignment(measure: true, commands: [shiftedMeasurementCommand]), pairing: pairing) { _ in }

        #expect(outcome == .delivered(jobId: 12, bytes: 15))
        let report = try #require(server.qualityReport)
        #expect((report["logs"] as? [String])?.count == 1)
        let measurement = try #require(recorder.all.first { $0.contains("-lavfi") })
        // The candidate's picture sits 41 ms into its container, the source's 21 ms: 20 ms to remove.
        #expect(measurement[13].contains("setpts=PTS-0.02*1000000,fps="))
    }

    @Test("a sampled measurement with no ffprobe to measure the lead reports nothing")
    func noProbeNoEvidence() async throws {
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 7, count: 4_096))
        let runner = JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            ffprobe: nil,
            runner: FakeTranscodeRunner(),
            scratchRoot: scratch(),
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        let outcome = await runner.execute(
            assignment(measure: true, commands: [shiftedMeasurementCommand]), pairing: pairing) { _ in }

        // Half an answer is worse than none: the server measures for itself.
        #expect(outcome == .delivered(jobId: 12, bytes: 15))
        #expect(server.qualityReport == nil)
    }

    @Test("a measurement that cannot be made reports nothing and the candidate is still delivered")
    func failedMeasurementStillDelivers() async throws {
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 7, count: 4_096))
        var fake = FakeTranscodeRunner()
        fake.measurementExitCode = 1
        let runner = JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: fake,
            scratchRoot: scratch(),
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        let outcome = await runner.execute(assignment(measure: true), pairing: pairing) { _ in }

        #expect(outcome == .delivered(jobId: 12, bytes: 15))
        #expect(server.qualityReport == nil)
    }

    @Test("a job with no gate measures nothing")
    func ungatedJobSkipsMeasurement() async throws {
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 7, count: 4_096))
        let runner = JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(),
            scratchRoot: scratch(),
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        _ = await runner.execute(assignment(), pairing: pairing) { _ in }

        #expect(server.qualityReport == nil)
    }
}

@Suite("Resumable delivery")
struct ResumableDeliveryTests {
    private func runner(_ server: FakeWorkerServer) -> JobRunner {
        JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(candidate: Data(repeating: 9, count: 200)),
            scratchRoot: scratch(),
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })
    }

    @Test("a server that offers resumable delivery receives the candidate in chunks and completes with both hashes")
    func deliversInChunks() async throws {
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 7, count: 4_096))
        server.resumable = true

        let outcome = await runner(server).execute(assignment(), pairing: pairing) { _ in }

        #expect(outcome == .delivered(jobId: 12, bytes: 200))
        #expect(server.completedViaChunks)
        #expect(server.deliveredFile == Data(repeating: 9, count: 200))
        #expect(server.deliveredHeaders["X-Optimisarr-Source-Sha256"] == server.sourceSha256)
    }

    @Test("a dropped chunk is retried from the offset the server reports, and nothing is sent twice")
    func resumesAfterADroppedChunk() async throws {
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 7, count: 4_096))
        server.resumable = true
        server.dropChunkAt = 0

        let outcome = await runner(server).execute(assignment(), pairing: pairing) { _ in }

        #expect(outcome == .delivered(jobId: 12, bytes: 200))
        // First attempt at 0 was dropped, the offset query said 0, the retry at 0 landed.
        #expect(server.chunkOffsets == [0, 0])
        #expect(server.deliveredFile == Data(repeating: 9, count: 200))
    }

    @Test("a server without resumable delivery still receives the whole file at once")
    func fallsBackToWholeFile() async throws {
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 7, count: 4_096))

        let outcome = await runner(server).execute(assignment(), pairing: pairing) { _ in }

        #expect(outcome == .delivered(jobId: 12, bytes: 200))
        #expect(!server.completedViaChunks)
        #expect(server.deliveredFile == Data(repeating: 9, count: 200))
    }
}
