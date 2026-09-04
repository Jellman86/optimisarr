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
            return (Data(), response(request, 404))
        }
    }

    func download(_ request: URLRequest, to destination: URL) async throws -> HTTPURLResponse {
        try sourceBytes.write(to: destination)
        return response(request, 200, headers: ["X-Optimisarr-Source-Sha256": sourceSha256])
    }

    func upload(_ request: URLRequest, fromFile file: URL) async throws -> (Data, HTTPURLResponse) {
        let delivered = try Data(contentsOf: file)
        return lock.withLock {
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

/// Stands in for ffmpeg: writes the candidate the command names, or fails, without encoding.
struct FakeTranscodeRunner: TranscodeRunner, @unchecked Sendable {
    var exitCode: Int32 = 0
    var candidate = Data("candidate bytes".utf8)
    var delay: TimeInterval = 0

    func run(
        _ executable: URL, _ arguments: [String], progress: @escaping @Sendable (Double) -> Void
    ) async throws -> (exitCode: Int32, stderr: String) {
        if delay > 0 { try await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000)) }
        progress(12.5)
        if exitCode == 0 {
            try candidate.write(to: URL(fileURLWithPath: arguments.last!))
        }
        return (exitCode, exitCode == 0 ? "" : "Error while opening encoder")
    }
}

private func assignment(renewWithinSeconds: Int = 30) -> Assignment {
    Assignment(
        leaseId: "8b1e2c3d-0000-4000-8000-000000000001", jobId: 12, sourceBytes: 4_096,
        videoEncoder: "hevc_videotoolbox", renewWithinSeconds: renewWithinSeconds,
        arguments: serverCommand, outputExtension: "mp4",
        quality: QualityRequirement(
            measure: true, model: "vmaf_v0.6.1", frameSubsample: 1, clipVmaf: false,
            minimumHarmonicMean: 93, minimumMinimum: 80))
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
        try await Task.sleep(nanoseconds: 200_000_000)

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
        try await Task.sleep(nanoseconds: 50_000_000)

        #expect(executor.executed.isEmpty)
        // Only heartbeats went out: a drained worker asking for work would be a wasted request.
        #expect(transport.heartbeats > 0)
        #expect(transport.claims == 0)
        session.unpair()
    }
}
