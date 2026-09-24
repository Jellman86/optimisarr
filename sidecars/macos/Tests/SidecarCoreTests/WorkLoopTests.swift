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

/// Holds the transfer until its task is cancelled. The renewal waits for the transfer to start,
/// so executor load cannot make the fake download finish before the test actually loses its lease.
private final class SuspendedSourceTransfer: Sendable {
    private let started = AsyncStream<Void>.makeStream()
    private let suspended = AsyncStream<Void>.makeStream()

    func waitUntilStarted() async throws {
        for await _ in started.stream {}
        try Task.checkCancellation()
    }

    func waitForCancellation() async throws {
        started.continuation.finish()
        for await _ in suspended.stream {}
        try Task.checkCancellation()
    }
}

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
    private(set) var sizeBudgetFailed = false
    private(set) var sizeBudgetUndershot = false
    private(set) var renewals = 0
    private(set) var qualityReport: [String: Any]?
    /// What the search answers, in order: the worker is told what to measure next until told to stop.
    var probeAnswers: [[String: Any]] = []
    var probeStatus = 200
    private(set) var probeReports: [[String: Any]] = []
    private(set) var renewalsWhenProbed: [Int] = []
    private(set) var qualityReportedBeforeDelivery = false
    /// Whether the server offers resumable delivery; off means an older server, whole-file only.
    var resumable = false
    /// Drop the chunk that starts at this offset once, as a failed connection would.
    var dropChunkAt: Int64? = nil
    /// Answer this many delivery calls with 502 before behaving, as a restarting container does.
    var deliveryBlinks = 0
    private(set) var deliveryRefusals = 0
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
    var beforeSourceCompletion: (@Sendable () async throws -> Void)?
    private(set) var sourceDownloadCompleted = false
    var deliveryDelay: TimeInterval = 0
    private(set) var deliveryCompleted = false

    /// A restarting container, one refusal at a time. Called with the lock already held.
    private func blink() -> Bool {
        guard deliveryBlinks > 0 else { return false }
        deliveryBlinks -= 1
        deliveryRefusals += 1
        return true
    }

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
            if path.hasSuffix("/size-budget-exceeded") {
                sizeBudgetFailed = true
                return (Data(), response(request, 204))
            }
            if path.hasSuffix("/size-budget-undershot") {
                sizeBudgetUndershot = true
                return (Data(), response(request, 204))
            }
            if path.hasSuffix("/result/offset") {
                guard resumable else { return (Data(), response(request, 404)) }
                return (Data("{\"bytes\":\(staged.count)}".utf8), response(request, 200))
            }
            if path.hasSuffix("/result"), request.httpMethod == "PATCH" {
                guard resumable else { return (Data(), response(request, 404)) }
                if blink() { return (Data(), response(request, 502)) }
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
                if blink() { return (Data(), response(request, 502)) }
                guard resumable else { return (Data(), response(request, 404)) }
                deliveredFile = staged
                deliveredHeaders = request.allHTTPHeaderFields ?? [:]
                completedViaChunks = true
                let body = deliverStatus == 202
                    ? Data(#"{"jobId":12,"bytes":\#(staged.count),"candidateSha256":"x"}"#.utf8)
                    : Data(#"{"error":"The uploaded candidate does not match the hash the worker declared."}"#.utf8)
                return (body, response(request, deliverStatus))
            }
            if path.hasSuffix("/quality-probe") {
                let body = try JSONSerialization.jsonObject(with: request.httpBody ?? Data()) as? [String: Any]
                probeReports.append(body ?? [:])
                if probeStatus != 200 {
                    return (Data(#"{"error":"The full encode is held for size review."}"#.utf8),
                            response(request, probeStatus))
                }
                // How many renewals had arrived by the time this candidate was reported. A total
                // taken at the end of the job cannot tell a search that renewed from one that did
                // not, because the encode that follows renews either way.
                renewalsWhenProbed.append(renewals)
                let answer = probeAnswers.isEmpty
                    ? [String: Any]()
                    : probeAnswers.removeFirst()
                return (try JSONSerialization.data(withJSONObject: answer), response(request, 200))
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
        try await beforeSourceCompletion?()
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

    @Test("each completed range moves the download bar, and the total is known from the first one")
    func reportsDownloadProgress() async throws {
        // Without a byte count the menu can only say "Fetching the source" for however long a
        // multi-gigabyte transfer takes, which is indistinguishable from being stuck.
        let bytes = Data((0..<200).map(UInt8.init))
        let server = FakeWorkerServer(sourceBytes: bytes)
        server.rangedSource = true
        let reports = ProgressRecorder()
        let runner = JobRunner(
            client: SidecarClient(transport: server, downloadChunkBytes: 64),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(),
            scratchRoot: scratch(),
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        _ = await runner.execute(assignment(sourceBytes: 200), pairing: pairing) { reports.record($0) }

        // The opening report is the assignment's own declared size, so the bar is drawn at zero
        // of the right total rather than appearing only once the first range lands.
        #expect(reports.downloads == [(0, 200), (64, 200), (128, 200), (192, 200), (200, 200)].map(Bytes.init))
    }

    @Test("a server that sends the whole file at once still reports a finished download")
    func reportsWholeFileDownload() async throws {
        // An older server answers 200 with the entire body. There is no range to count, but the
        // bar must still finish rather than sit at zero until the encode starts.
        let bytes = Data((0..<200).map(UInt8.init))
        let server = FakeWorkerServer(sourceBytes: bytes)
        let reports = ProgressRecorder()
        let runner = JobRunner(
            client: SidecarClient(transport: server, downloadChunkBytes: 64),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(),
            scratchRoot: scratch(),
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        _ = await runner.execute(assignment(sourceBytes: 200), pairing: pairing) { reports.record($0) }

        #expect(reports.downloads.last == Bytes(200, 200))
    }

    @Test("each delivered chunk moves the upload bar")
    func reportsUploadProgress() async throws {
        let server = FakeWorkerServer(sourceBytes: Data((0..<200).map(UInt8.init)))
        server.resumable = true
        let reports = ProgressRecorder()
        let runner = JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(candidate: Data(repeating: 7, count: 200)),
            scratchRoot: scratch(),
            chunkBytes: 64,
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        _ = await runner.execute(assignment(sourceBytes: 200), pairing: pairing) { reports.record($0) }

        #expect(reports.uploads == [(0, 200), (64, 200), (128, 200), (192, 200), (200, 200)].map(Bytes.init))
    }
}

@Suite("Timeline lead")
struct TimelineLeadShiftTests {
    @Test("a gap under half a microsecond is written as plain zero")
    func tinyGapIsZero() {
        // The server parses this back out of the filter graph, so "0" rather than "0.0000004".
        #expect(TimelineLead.shift(candidate: 0.0410002, source: 0.041) == "0")
        #expect(TimelineLead.shift(candidate: 0.041, source: 0.041) == "0")
    }

    @Test("a real gap is written to the microsecond, without trailing zeros")
    func realGapIsKept() {
        // The case seen on 2026-09-14: a source whose video starts 31 ms after its container and a
        // candidate that starts at 41 ms.
        #expect(TimelineLead.shift(candidate: 0.041016, source: 0.031) == "0.010016")
        #expect(TimelineLead.shift(candidate: 0.041, source: 0.0) == "0.041")
    }

    /// The same table is asserted in the Windows sidecar's own suite
    /// (`TimelineLeadSharedTableTests`). Both workers report to one server, which parses what comes
    /// back, so the two implementations agreeing is a contract rather than a coincidence — and the
    /// only way to hold two languages to it is to write the answers down once and check them in
    /// both places. Change one side and this fails; change the rule and change both tables.
    @Test("both sidecars write the same shift")
    func theSharedTable() {
        let cases: [(candidate: Double, source: Double, expected: String)] = [
            (0.042, 0, "0.042"),
            (0, 0.042, "-0.042"),
            (1.5, 1, "0.5"),
            (0.0000001, 0, "0"),
            (1.0, 1.0, "0"),
            (0.9999999, 1.0, "0"),
            (2, 2, "0"),
            (0.1, 0, "0.1"),
            (0.0416667, 0, "0.041667"),
            (-0.5, 0.25, "-0.75"),
            (0.0000004, 0, "0"),
            (0.0000006, 0, "0.000001"),
        ]
        for entry in cases {
            #expect(
                TimelineLead.shift(candidate: entry.candidate, source: entry.source) == entry.expected,
                "candidate \(entry.candidate), source \(entry.source)")
        }
    }


    @Test("a candidate earlier than its source keeps its sign")
    func negativeGapIsKept() {
        #expect(TimelineLead.shift(candidate: 0.021, source: 0.031) == "-0.01")
    }
}

@Suite("Work location in a real job")
struct WorkLocationJobTests {
    @Test("a job too large for the memory budget runs on disk instead of being handed back")
    func fallsBackToDiskRatherThanRefusing() async throws {
        // The point of the fallback: a preference must never cost the work. Before, a job that did
        // not fit was released, and a released job goes straight back on the queue to be offered
        // again — a loop caused by a setting.
        let server = FakeWorkerServer(sourceBytes: Data((0..<200).map(UInt8.init)))
        let scratch = scratch()
        let runner = JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(),
            scratchRoot: scratch,
            // A budget of one byte: nothing can fit, so every job must take the disk path.
            settings: SettingsSnapshot(workLocation: .memory, memoryBudgetBytes: 1),
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        let outcome = await runner.execute(assignment(sourceBytes: 200), pairing: pairing) { _ in }

        #expect(outcome == .delivered(jobId: 12, bytes: 15))
        #expect(!server.released)
    }

    @Test("a chosen folder is where the job actually works")
    func usesTheChosenFolder() async throws {
        let server = FakeWorkerServer(sourceBytes: Data((0..<200).map(UInt8.init)))
        let chosen = scratch()
        let unused = scratch()
        let runner = JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(),
            scratchRoot: unused,
            settings: SettingsSnapshot(workLocation: .folder(chosen)),
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        let outcome = await runner.execute(assignment(sourceBytes: 200), pairing: pairing) { _ in }

        #expect(outcome == .delivered(jobId: 12, bytes: 15))
        // Scratch is removed on every exit path, so what is checked is that the default root was
        // never used rather than that the chosen one still holds anything.
        let leftInDefault = (try? FileManager.default.contentsOfDirectory(atPath: unused.path)) ?? []
        #expect(leftInDefault.isEmpty)
    }
}

/// A pair of byte counts, so a test can state the whole expected sequence in one line.
struct Bytes: Equatable {
    let done: Int64
    let total: Int64
    init(_ done: Int64, _ total: Int64) { self.done = done; self.total = total }
}

/// Collects the progress a job reported, split by the two transfer stages.
final class ProgressRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var all: [JobProgress] = []

    func record(_ progress: JobProgress) { lock.withLock { all.append(progress) } }

    var downloads: [Bytes] {
        lock.withLock {
            all.compactMap { if case let .fetchingSource(received, total) = $0 { Bytes(received, total) } else { nil } }
        }
    }

    var uploads: [Bytes] {
        lock.withLock {
            all.compactMap { if case let .delivering(sent, total) = $0 { Bytes(sent, total) } else { nil } }
        }
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
    var sizeBudgetExceededAtBytes: Int64?

    func run(
        _ executable: URL, _ arguments: [String], sizeBudget: OutputSizeBudget?,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> (exitCode: Int32, stderr: String) {
        if let sizeBudget, let observed = sizeBudgetExceededAtBytes {
            throw OutputSizeExceeded(observedBytes: observed, maxBytes: sizeBudget.maxBytes)
        }
        return try await run(executable, arguments, progress: progress)
    }

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
    commands: [[String]] = [measurementCommand], maxCandidateBytes: Int64? = nil,
    minCandidateBytes: Int64? = nil
) -> Assignment {
    Assignment(
        leaseId: "8b1e2c3d-0000-4000-8000-000000000001", jobId: 12, sourceBytes: sourceBytes,
        videoEncoder: "hevc_videotoolbox", renewWithinSeconds: renewWithinSeconds,
        arguments: serverCommand, outputExtension: "mp4",
        quality: QualityRequirement(
            measure: measure, model: "vmaf_v0.6.1", frameSubsample: 1, clipVmaf: false,
            minimumHarmonicMean: 93, minimumMinimum: 80,
            commands: measure ? commands : [], sampling: "Full file"),
        maxCandidateBytes: maxCandidateBytes, minCandidateBytes: minCandidateBytes)
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
    @Test("an oversized candidate is reported as terminal instead of handed to another worker")
    func sizeBudgetStopsJob() async throws {
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 7, count: 4_096))
        let root = scratch()
        let runner = JobRunner(client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(sizeBudgetExceededAtBytes: 4_096),
            scratchRoot: root,
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        let outcome = await runner.execute(assignment(maxCandidateBytes: 4_095), pairing: pairing) { _ in }

        guard case let .failed(jobId, reason) = outcome else {
            Issue.record("expected a terminal size failure, got \(outcome)")
            return
        }
        #expect(jobId == 12)
        #expect(reason.contains("Size saving"))
        #expect(server.sizeBudgetFailed)
        #expect(!server.released)
        #expect(server.deliveredFile == nil)
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("job-12").path))
    }

    @Test("a successful encode whose final mux exceeds its budget is rejected before quality work")
    func finalMuxSizeBudgetStopsJob() async throws {
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 7, count: 4_096))
        let root = scratch()
        let runner = JobRunner(client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(),
            scratchRoot: root,
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        let outcome = await runner.execute(assignment(maxCandidateBytes: 14), pairing: pairing) { _ in }

        guard case let .failed(_, reason) = outcome else {
            Issue.record("expected a terminal size failure, got \(outcome)")
            return
        }
        #expect(reason.contains("Size saving"))
        #expect(server.sizeBudgetFailed)
        #expect(!server.released)
        #expect(server.deliveredFile == nil)
    }

    @Test("an over-compressed final candidate fails before quality work or delivery")
    func finalCompressionCeilingStopsJob() async throws {
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 7, count: 4_096))
        let runner = JobRunner(client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(), scratchRoot: scratch(),
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        let outcome = await runner.execute(assignment(minCandidateBytes: 16), pairing: pairing) { _ in }

        guard case let .failed(_, reason) = outcome else {
            Issue.record("expected a terminal compression failure, got \(outcome)")
            return
        }
        #expect(reason.contains("Compression ceiling"))
        #expect(server.sizeBudgetUndershot)
        #expect(!server.released)
        #expect(server.deliveredFile == nil)
    }

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

    @Test("a server that blinks mid-encode does not throw away the encode")
    func serverBlinkKeepsTheEncode() async throws {
        // A deployment restarts the container and the proxy answers 503 for a few seconds. Every
        // encode running at that moment used to be abandoned, because one refused renewal ended
        // the renewal loop and took the job with it. A refused renewal says nothing about whether
        // the lease survives; only how long it has been since one landed does.
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 1, count: 64))
        server.renewStatus = 503
        let runner = JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(delay: 1),
            scratchRoot: scratch(),
            sleep: { _ in try await Task.sleep(nanoseconds: 50_000_000) })

        // A ten-second window against a one-second encode: the server is unreachable throughout
        // and the lease still has not been out of touch for long enough to be given up.
        let outcome = await runner.execute(assignment(renewWithinSeconds: 10), pairing: pairing) { _ in }

        #expect(server.renewals > 0, "the test proves nothing if no renewal was attempted")
        guard case .delivered = outcome else {
            Issue.record("expected the candidate to be delivered, got \(outcome)")
            return
        }
    }

    @Test("a server that stays away for longer than the lease window gives the job up")
    func serverAwayTooLongEndsTheJob() async throws {
        // The other end of the same rule. Tolerating a blink must not become encoding for an hour
        // against a lease that lapsed in the first minute, so the tolerance is exactly the window
        // the lease was granted for and not a second more.
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 1, count: 64))
        server.renewStatus = 503
        let runner = JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(delay: 5),
            scratchRoot: scratch(),
            sleep: { _ in try await Task.sleep(nanoseconds: 50_000_000) })

        let started = Date()
        let outcome = await runner.execute(assignment(renewWithinSeconds: 1), pairing: pairing) { _ in }

        guard case .leaseLost = outcome else {
            Issue.record("expected the lease to be given up, got \(outcome)")
            return
        }
        // Given up on the window, not on the encode finishing.
        #expect(Date().timeIntervalSince(started) < 4)
        #expect(server.deliveredFile == nil)
    }

    @Test("a finished candidate is not thrown away because the server is restarting")
    func deliveryOutlastsARestart() async throws {
        // The Mac's own words on the evening this was found: "handed back — The server replied
        // unexpectedly (HTTP 502)". A complete encode, measured and accepted, binned because a
        // deployment restarted the container while its bytes were going up. Only `transferFailed`
        // was being retried, so a 502 arrived as `unexpectedResponse` and went uncaught.
        let bytes = Data((0..<200).map(UInt8.init))
        let server = FakeWorkerServer(sourceBytes: bytes)
        server.resumable = true
        server.deliveryBlinks = 3
        let runner = JobRunner(
            client: SidecarClient(transport: server, downloadChunkBytes: 64),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(),
            scratchRoot: scratch(),
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        let outcome = await runner.execute(assignment(sourceBytes: 200), pairing: pairing) { _ in }

        #expect(server.deliveryRefusals == 3, "the test proves nothing if the server never blinked")
        guard case .delivered = outcome else {
            Issue.record("expected the candidate to be delivered, got \(outcome)")
            return
        }
        #expect(server.deliveredFile != nil)
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

    @Test("losing the lease during a source transfer cancels the download", .timeLimit(.minutes(1)))
    func lostLeaseCancelsSourceTransfer() async throws {
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 1, count: 64))
        let transfer = SuspendedSourceTransfer()
        server.beforeSourceCompletion = { try await transfer.waitForCancellation() }
        server.renewStatus = 409
        let runner = JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(),
            scratchRoot: scratch(),
            load: MachineLoadSampler(gpu: { nil }),
            sleep: { _ in try await transfer.waitUntilStarted() })

        let outcome = await runner.execute(
            assignment(renewWithinSeconds: 10), pairing: pairing) { _ in }

        guard case .leaseLost = outcome else {
            Issue.record("expected the lease to be lost, got \(outcome)")
            return
        }
        #expect(server.sourceDownloads == 1)
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
        progress: @escaping @Sendable (JobProgress) -> Void,
        preview: @escaping @Sendable (Data) -> Void
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
    /// The output limit a command ran with: 5 for an alignment probe, 40 for a window.
    static func limit(of arguments: [String]) -> String? {
        arguments.firstIndex(of: "-t").map { arguments[$0 + 1] }
    }

    /// The candidate shift substituted into a command's filter graph.
    static func shift(in arguments: [String]) -> String? {
        guard let graph = arguments.first(where: { $0.contains("setpts=PTS-") }),
              let start = graph.range(of: "setpts=PTS-")?.upperBound,
              let end = graph[start...].range(of: "*1000000")?.lowerBound
        else { return nil }
        return String(graph[start..<end])
    }

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
        // Tried, not derived: the server's own measurement, cut to a few seconds, once per
        // candidate offset. A probe with a graph of its own chose offsets that were a frame wrong
        // for the measurement that followed, and failed clean encodes at harmonic 9.
        let probes = recorder.all.filter { Self.limit(of: $0) == "5" }
        #expect(probes.count == TimelineAlignment.framesToTry.count)
        #expect(probes.allSatisfy { $0[5] == "113.008875" && $0[13].contains(",fps=fps=23.976023976023978:start_time=0,trim=") })
        // One frame either way at the fake source's 25 fps.
        #expect(probes.map { Self.shift(in: $0) } == ["0", "0.04", "-0.04"])

        let measurement = try #require(recorder.all.last { Self.limit(of: $0) == "40" })
        // Every offset scores alike against this fake, so the one that changes nothing wins.
        #expect(measurement[13].contains("setpts=PTS-0*1000000,fps="))
    }

    @Test("each quality window probes its own picture alignment")
    func alignsEachQualityWindow() async throws {
        var second = shiftedMeasurementCommand
        second = second.map {
            $0.replacingOccurrences(of: "113.008875", with: "1414.996917")
                .replacingOccurrences(of: "4.991125", with: "5.003083")
        }
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 7, count: 4_096))
        let recorder = ArgumentRecorder()
        var fake = FakeTranscodeRunner()
        fake.recorder = recorder
        let runner = JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            ffprobe: URL(fileURLWithPath: "/usr/bin/true"),
            runner: fake, leadProbe: FakeLeadProbe(), scratchRoot: scratch(),
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        let outcome = await runner.execute(
            assignment(measure: true, commands: [shiftedMeasurementCommand, second]),
            pairing: pairing) { _ in }

        #expect(outcome == .delivered(jobId: 12, bytes: 15))
        // Each window is probed where it will be scored, because a frame lost between windows
        // moves the later one and not the earlier.
        let probes = recorder.all.filter { Self.limit(of: $0) == "5" }
        #expect(probes.count == 2 * TimelineAlignment.framesToTry.count)
        #expect(probes[0][probes[0].firstIndex(of: "-ss")! + 1] == "113.008875")
        #expect(probes[3][probes[3].firstIndex(of: "-ss")! + 1] == "1414.996917")
    }

    @Test("a sampled measurement whose alignment cannot be scored reports nothing")
    func noAlignmentNoEvidence() async throws {
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 7, count: 4_096))
        let runner = JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            ffprobe: nil,
            // Every probe fails, so no offset can be chosen — and a guessed one would misalign the
            // comparison it exists to align.
            runner: FakeTranscodeRunner(measurementExitCode: 1),
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

/// The command the search settles on: the same shape, at the quality it chose rather than the
/// library's. Distinct from `serverCommand` so a test can tell which one was actually run.
private let settledCommand: [String] = serverCommand.map { $0 == "60" ? "48" : $0 }

@Suite("Per-title quality search")
struct AdaptiveSearchWorkLoopTests {
    /// The server names every candidate; this machine measures them and reports.
    private static func step(quality: Int) -> AdaptiveSearchStep {
        AdaptiveSearchStep(
            quality: quality,
            sampleCommands: [serverCommand],
            measurement: QualityRequirement(
                measure: true, model: "vmaf_v0.6.1", frameSubsample: 1, clipVmaf: true,
                minimumHarmonicMean: 93, minimumMinimum: 80,
                commands: [measurementCommand], sampling: "Adaptive sample at quality \(quality)"))
    }

    private static func searching(_ first: AdaptiveSearchStep) -> Assignment {
        Assignment(
            leaseId: "8b1e2c3d-0000-4000-8000-000000000001", jobId: 12, sourceBytes: 4_096,
            videoEncoder: "hevc_videotoolbox", renewWithinSeconds: 30,
            arguments: serverCommand, outputExtension: "mp4",
            quality: QualityRequirement(
                measure: false, model: "vmaf_v0.6.1", frameSubsample: 1, clipVmaf: false,
                minimumHarmonicMean: 93, minimumMinimum: 80, commands: [], sampling: "Full file"),
            search: first)
    }

    @Test("every candidate the server names is measured, and the encode uses what it chose")
    func measuresUntilToldToStop() async throws {
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 3, count: 64))
        // Measure one more, then stop — and hand back a different command to encode with.
        server.probeAnswers = [
            ["nextStep": [
                "quality": 30,
                "sampleCommands": [serverCommand],
                "measurement": [
                    "measure": true, "model": "vmaf_v0.6.1", "frameSubsample": 1, "clipVmaf": true,
                    "minimumHarmonicMean": 93.0, "minimumMinimum": 80.0,
                    "commands": [measurementCommand], "sampling": "Adaptive sample at quality 30",
                ],
            ]],
            ["selectedQuality": 27, "arguments": settledCommand],
        ]

        let runner = JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(),
            scratchRoot: scratch(),
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        let outcome = await runner.execute(
            Self.searching(Self.step(quality: 24)), pairing: pairing) { _ in }

        // Two candidates measured, each reported with the quality it was asked for.
        #expect(server.probeReports.count == 2)
        #expect(server.probeReports.map { $0["quality"] as? Int } == [24, 30])
        // Bytes come from the samples this machine actually encoded, never invented.
        #expect(server.probeReports.allSatisfy { ($0["encodedBytes"] as? Int ?? 0) > 0 })
        // Split by window too, so the server's size forecast can say which scenes grew. The split
        // covers every sample and adds up to the total, or the server would refuse the report.
        #expect(server.probeReports.allSatisfy { report in
            guard let split = report["windowEncodedBytes"] as? [Int] else { return false }
            // The fixture's step has one sample window.
            return split.count == 1 && split.allSatisfy { $0 > 0 }
                && split.reduce(0, +) == report["encodedBytes"] as? Int
        })
        // And the job still completes, encoded with the command the search settled on rather than
        // the one the assignment arrived carrying.
        #expect(outcome == .delivered(jobId: 12, bytes: 15))
    }

    @Test("a search that cannot be measured hands the job back rather than guessing a quality")
    func failedMeasurementReleases() async throws {
        // Encoding at the assignment's baseline instead would discard the search and produce a
        // file at a quality nobody chose, which would look like a perfectly successful job.
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 3, count: 64))
        let runner = JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(exitCode: 1),
            scratchRoot: scratch(),
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        let outcome = await runner.execute(
            Self.searching(Self.step(quality: 24)), pairing: pairing) { _ in }

        #expect(server.released)
        #expect(server.deliveredFile == nil)
        if case .released = outcome {} else { Issue.record("expected the job to be handed back") }
    }

    @Test("size review from the server stops a worker before the full encode")
    func sizeReviewStopsBeforeEncode() async throws {
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 3, count: 64))
        server.probeStatus = 409
        let runner = JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(),
            scratchRoot: scratch(),
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        let outcome = await runner.execute(
            Self.searching(Self.step(quality: 24)), pairing: pairing) { _ in }

        #expect(server.probeReports.count == 1)
        #expect(server.deliveredFile == nil)
        #expect(!server.released)
        if case .leaseLost = outcome {} else { Issue.record("expected size review to end the lease, got \(outcome)") }
    }

    @Test("the lease is renewed while a candidate is being measured")
    func renewsWhileMeasuring() async throws {
        // A search is the longest thing this machine does before it has anything to show: several
        // sample encodes and a VMAF pass each. It ran outside the renewal loop, so the server heard
        // nothing throughout and the lease lapsed — which is exactly what the expired lease on the
        // first search ever run against real hardware records.
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 3, count: 64))
        server.probeAnswers = [["selectedQuality": 24, "arguments": settledCommand]]

        let runner = JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            // Long enough that several renewal ticks fall inside the measurement.
            runner: FakeTranscodeRunner(delay: 0.3),
            scratchRoot: scratch(),
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        _ = await runner.execute(
            Self.searching(Self.step(quality: 24)), pairing: pairing) { _ in }

        // Counted when the candidate was reported, not at the end: the encode that follows a search
        // renews either way, so a total taken afterwards cannot tell the two apart.
        #expect(server.probeReports.count == 1)
        #expect(server.renewalsWhenProbed.first ?? 0 > 0)
    }

    @Test("a lease lost while measuring stops the search rather than measuring on")
    func lostLeaseStopsTheSearch() async throws {
        // Every later candidate is derived from this one, so a search carried on under a dead lease
        // is work nobody will accept the answer to.
        let server = FakeWorkerServer(sourceBytes: Data(repeating: 3, count: 64))
        server.renewStatus = 409
        let runner = JobRunner(
            client: SidecarClient(transport: server),
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: FakeTranscodeRunner(delay: 5),
            scratchRoot: scratch(),
            sleep: { _ in try await Task.sleep(nanoseconds: 1_000_000) })

        let started = Date()
        let outcome = await runner.execute(
            Self.searching(Self.step(quality: 24)), pairing: pairing) { _ in }

        #expect(Date().timeIntervalSince(started) < 4)
        #expect(server.deliveredFile == nil)
        // A 409 means the server has already revoked the lease. There is nothing left to
        // release; timing must not turn that explicit loss into a handback expectation.
        if case .leaseLost = outcome {} else { Issue.record("expected the expired lease to be reported, got \(outcome)") }
    }
}

@Suite("Timeline alignment choice")
struct TimelineAlignmentChoiceTests {
    @Test("a probe is the measurement itself, stopped after a few seconds")
    func truncatesTheRealCommand() {
        let command = ["-ss", "113.008875", "-i", "cand.mp4", "-lavfi", "graph", "-t", "40", "-f", "null", "-"]
        #expect(TimelineAlignment.truncate(command, seconds: 5)
            == ["-ss", "113.008875", "-i", "cand.mp4", "-lavfi", "graph", "-t", "5", "-f", "null", "-"])
        // A full-file measurement has no limit of its own, so one is added before the output.
        #expect(TimelineAlignment.truncate(["-i", "cand.mp4", "-lavfi", "graph", "-f", "null", "-"], seconds: 5)
            == ["-i", "cand.mp4", "-lavfi", "graph", "-t", "5", "-f", "null", "-"])
    }

    @Test("a clearly better offset wins, and a near tie keeps the unshifted timeline")
    func choosesWithAMargin() {
        #expect(TimelineAlignment.choose([(0, 96.2), (1, 19.7), (-1, 20.1)]) == 0)
        #expect(TimelineAlignment.choose([(0, 0.2), (1, 92.3), (-1, 0.3)]) == 1)
        // A static shot scores alike at every shift; noise must not move the whole window.
        #expect(TimelineAlignment.choose([(0, 95.1), (1, 96.4), (-1, 94.8)]) == 0)
        #expect(TimelineAlignment.choose([]) == nil)
    }

    @Test("both sidecars and the server try the same offsets for the same time")
    func sameOffsetsEverywhere() {
        #expect(TimelineAlignment.framesToTry == [0, 1, -1])
        #expect(TimelineAlignment.probeSeconds == 5)
        #expect(TimelineAlignment.choiceMarginPoints == 2)
    }
}
