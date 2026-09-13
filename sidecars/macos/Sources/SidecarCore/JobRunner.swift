import CryptoKit
import Foundation

/// A line of FFmpeg's `-progress pipe:1` protocol, reduced to the one number the menu shows.
///
/// Every field arrives as `key=value`, one per line, and a block ends with `progress=continue`
/// or `progress=end`. Only the encoded time is read; the server owns the frame arithmetic.
public enum FfmpegProgressLine {
    /// Seconds of output produced so far, when the line carries it.
    public static func encodedSeconds(_ line: String) -> Double? {
        let trimmed = line.trimmingCharacters(in: .whitespaces)
        // out_time_us is microseconds. out_time_ms is, despite its name, also microseconds in
        // every FFmpeg that prints it; both are read the same way so a build that drops one
        // still reports progress.
        for key in ["out_time_us=", "out_time_ms="] where trimmed.hasPrefix(key) {
            guard let micro = Double(trimmed.dropFirst(key.count)), micro >= 0 else { return nil }
            return micro / 1_000_000
        }
        return nil
    }
}

/// Runs the transcode itself, so the job flow can be tested with a runner that merely writes a
/// file. Cancellation of the calling task must stop the process: a lease that is lost or a
/// pairing that is forgotten must not leave an encode running for nobody.
public protocol TranscodeRunner: Sendable {
    func run(
        _ executable: URL,
        _ arguments: [String],
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> (exitCode: Int32, stderr: String)
}

public struct ProcessTranscodeRunner: TranscodeRunner {
    public init() {}

    /// Process and its pipes are thread-safe Foundation objects that the compiler cannot see as
    /// such; the box lets the cancellation handler reach the process to terminate it.
    private final class RunningProcess: @unchecked Sendable {
        let process = Process()
        let stdout = Pipe()
        let stderr = Pipe()
    }

    public func run(
        _ executable: URL,
        _ arguments: [String],
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> (exitCode: Int32, stderr: String) {
        let running = RunningProcess()
        running.process.executableURL = executable
        running.process.arguments = arguments
        running.process.standardOutput = running.stdout
        running.process.standardError = running.stderr

        try running.process.run()

        return try await withTaskCancellationHandler {
            let stderrTask = Task.detached {
                running.stderr.fileHandleForReading.readDataToEndOfFile()
            }
            for try await line in running.stdout.fileHandleForReading.bytes.lines {
                if let seconds = FfmpegProgressLine.encodedSeconds(line) {
                    progress(seconds)
                }
            }
            let errorOutput = await stderrTask.value
            running.process.waitUntilExit()
            return (
                running.process.terminationStatus,
                String(decoding: errorOutput.suffix(4_096), as: UTF8.self))
        } onCancel: {
            running.process.terminate()
        }
    }
}

/// How one assignment ended, in terms the menu can show and a log can keep.
public enum JobOutcome: Sendable, Equatable {
    /// The candidate reached the server intact. Verification there decides what it is worth.
    case delivered(jobId: Int, bytes: Int64)

    /// The job was handed back for the server to reassign, with the reason it could not be done.
    case released(jobId: Int, reason: String)

    /// The lease lapsed or was refused mid-job; the server has already moved on.
    case leaseLost(jobId: Int, reason: String)
}

/// What is happening inside a running job, for the menu.
///
/// The two transfer stages carry byte counts because they are the ones that can take a long time
/// with nothing else to show: a multi-gigabyte source over a home network looks identical to a
/// stalled one unless the bytes are counted. A total of zero means it is not known yet, which is
/// true for the moment before the first range comes back and for a server that answers with the
/// whole file and no range header.
public enum JobProgress: Sendable, Equatable {
    case fetchingSource(received: Int64, total: Int64)
    case encoding(encodedSeconds: Double)
    case measuring
    case delivering(sent: Int64, total: Int64)
}

/// The most recent progress, shared between the ffmpeg reader and the renewal loop. A lock
/// rather than an actor because the reader is a synchronous callback on ffmpeg's pipe.
final class LatestProgress: @unchecked Sendable {
    private let lock = NSLock()
    private var value: JobProgress

    init(_ initial: JobProgress) { value = initial }

    func set(_ progress: JobProgress) {
        lock.lock(); defer { lock.unlock() }
        value = progress
    }

    func get() -> JobProgress {
        lock.lock(); defer { lock.unlock() }
        return value
    }
}

private enum LeaseOperation<T: Sendable>: Sendable {
    case completed(T)
    case renewalStopped
}

/// Executes assignments, so the session can be driven in tests without an ffmpeg or a server.
///
/// `preview` carries an occasional JPEG of the frame being encoded. It is separate from `progress`
/// so the stage enum stays small and cheap to compare: previews arrive rarely and are worth
/// several kilobytes each, while progress arrives many times a second.
public protocol WorkExecutor: Sendable {
    func execute(
        _ assignment: Assignment,
        pairing: StoredPairing,
        progress: @escaping @Sendable (JobProgress) -> Void,
        preview: @escaping @Sendable (Data) -> Void
    ) async -> JobOutcome
}

/// Takes one assignment from claim to delivery.
///
/// The order is chosen for what each step protects. The command is validated before a byte is
/// fetched, so nothing this machine will not run is ever downloaded for. The source is hashed on
/// arrival and compared to the server's hash, so a corrupt transfer is never encoded and passed
/// off as a candidate. The lease is renewed throughout, and losing it cancels the encode rather
/// than finishing work the server has already given to someone else. Everything lives under one
/// scratch directory per lease and is removed on every exit path, so a failed job leaves nothing
/// behind but a line in the log.
public struct JobRunner: WorkExecutor {
    private let client: SidecarClient
    private let ffmpeg: URL?
    private let ffprobe: URL?
    private let runner: TranscodeRunner
    private let leadProbe: CommandRunner
    private let scratchRoot: URL
    private let chunkBytes: Int64
    private let previewSampler: FramePreviewSampler?
    private let wantsPreviews: @Sendable () -> Bool
    private let availableScratchBytes: @Sendable (URL) -> Int64?
    private let sleep: @Sendable (TimeInterval) async throws -> Void

    public init(
        client: SidecarClient = SidecarClient(),
        ffmpeg: URL? = CapabilityProber.bundledFfmpeg(),
        ffprobe: URL? = CapabilityProber.bundledFfprobe(),
        runner: TranscodeRunner = ProcessTranscodeRunner(),
        leadProbe: CommandRunner = ProcessCommandRunner(),
        scratchRoot: URL = JobRunner.defaultScratchRoot(),
        chunkBytes: Int64 = JobRunner.defaultChunkBytes,
        previewSampler: FramePreviewSampler? = CapabilityProber.bundledFfmpeg().map { FramePreviewSampler(ffmpeg: $0) },
        wantsPreviews: @escaping @Sendable () -> Bool = { false },
        availableScratchBytes: @escaping @Sendable (URL) -> Int64? = JobRunner.availableScratchBytes,
        sleep: @escaping @Sendable (TimeInterval) async throws -> Void = { seconds in
            try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
        }
    ) {
        self.client = client
        self.ffmpeg = ffmpeg
        self.ffprobe = ffprobe
        self.runner = runner
        self.leadProbe = leadProbe
        self.scratchRoot = scratchRoot
        self.chunkBytes = max(1, chunkBytes)
        self.previewSampler = previewSampler
        self.wantsPreviews = wantsPreviews
        self.availableScratchBytes = availableScratchBytes
        self.sleep = sleep
    }

    /// Scratch under the app's own support directory rather than a shared temp location, so a
    /// multi-gigabyte source is somewhere an operator can find, and never in anyone else's way.
    public static func defaultScratchRoot() -> URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        return base.appendingPathComponent("OptimisarrSidecar/work", isDirectory: true)
    }

    /// Capacity of the volume that will actually hold work. The app-support path may not exist on
    /// first launch, so walk to its nearest existing ancestor rather than reporting zero from a
    /// URL whose volume metadata cannot yet be read.
    public static func availableScratchBytes(at directory: URL) -> Int64? {
        var existing = directory
        while !FileManager.default.fileExists(atPath: existing.path) {
            let parent = existing.deletingLastPathComponent()
            guard parent != existing else { return nil }
            existing = parent
        }
        let values = try? existing.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey])
        return values?.volumeAvailableCapacityForImportantUsage
    }

    public func execute(
        _ assignment: Assignment,
        pairing: StoredPairing,
        progress: @escaping @Sendable (JobProgress) -> Void,
        preview: @escaping @Sendable (Data) -> Void = { _ in }
    ) async -> JobOutcome {
        let scratch = scratchRoot.appendingPathComponent("lease-\(assignment.leaseId)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: scratch) }

        do {
            let outcome = try await run(
                assignment, pairing: pairing, scratch: scratch, progress: progress, preview: preview)
            return outcome
        } catch let error as SidecarError {
            switch error {
            case let .leaseLost(reason):
                return .leaseLost(jobId: assignment.jobId, reason: reason)
            case .credentialRejected:
                return .leaseLost(jobId: assignment.jobId, reason: "This worker's credential was rejected.")
            default:
                return await release(assignment, pairing: pairing, reason: Self.describe(error))
            }
        } catch is CancellationError {
            return await release(assignment, pairing: pairing, reason: "The job was cancelled on this machine.")
        } catch {
            return await release(assignment, pairing: pairing, reason: error.localizedDescription)
        }
    }

    /// Bytes per chunk of a resumable upload. Large enough that a film is a few dozen requests,
    /// small enough that a dropped connection loses a minute, not an hour.
    public static let defaultChunkBytes: Int64 = 64 * 1024 * 1024

    /// How many times a chunk may fail before the delivery is given up.
    static let maxChunkFailures = 20

    /// Source ranges use the same bounded retry budget as candidate chunks. A transient network
    /// failure keeps the complete ranges already on disk; an exhausted budget hands the lease
    /// back instead of retrying forever.
    static let maxSourceFailures = 20

    private func fetchSource(
        _ assignment: Assignment, pairing: StoredPairing, destination: URL,
        progress: @escaping @Sendable (Int64, Int64) -> Void
    ) async throws -> String {
        var failures = 0
        while true {
            try Task.checkCancellation()
            do {
                return try await client.fetchSource(
                    serverAddress: pairing.serverAddress, credential: pairing.credential,
                    leaseId: assignment.leaseId, to: destination, progress: progress)
            } catch SidecarError.transferFailed {
                failures += 1
                guard failures <= Self.maxSourceFailures else {
                    throw SidecarError.transferFailed(reason: "The source download failed \(failures) times.")
                }
                try await sleep(min(30, Double(failures) * 2))
            }
        }
    }

    /// Runs a potentially long stage beside the lease heartbeat. Whichever finishes first stops
    /// the other: a completed stage no longer needs its renewal loop, while a refused renewal
    /// cancels the transfer or process immediately so this Mac does no more work under a dead
    /// lease. This wraps fetching, encoding, measuring, and delivery — not only ffmpeg.
    private func whileRenewingLease<T: Sendable>(
        _ assignment: Assignment,
        pairing: StoredPairing,
        progress: @escaping @Sendable () -> JobProgress,
        operation: @escaping @Sendable () async throws -> T
    ) async throws -> T {
        try await withThrowingTaskGroup(of: LeaseOperation<T>.self) { group in
            group.addTask { .completed(try await operation()) }
            group.addTask {
                let interval = min(15, max(5, Double(assignment.renewWithinSeconds) / 2))
                while !Task.isCancelled {
                    try await sleep(interval)
                    try await client.renew(
                        serverAddress: pairing.serverAddress, credential: pairing.credential,
                        leaseId: assignment.leaseId, progress: progress())
                }
                return .renewalStopped
            }

            defer { group.cancelAll() }
            guard let first = try await group.next() else { throw CancellationError() }
            switch first {
            case let .completed(value): return value
            case .renewalStopped: throw CancellationError()
            }
        }
    }

    /// Delivers the candidate in chunks the server confirms, resuming from whatever it holds
    /// after a failure; a server that predates resumable delivery gets the whole file at once.
    private func deliver(
        _ assignment: Assignment, pairing: StoredPairing, candidate: URL,
        sourceSha256: String, candidateSha256: String,
        progress: @escaping @Sendable (Int64, Int64) -> Void
    ) async throws -> DeliveryReceipt {
        guard var offset = try await client.uploadOffset(
            serverAddress: pairing.serverAddress, credential: pairing.credential, leaseId: assignment.leaseId)
        else {
            // An older server takes the whole file in one request, so there is nothing to count
            // along the way; the bar fills when it lands.
            let receipt = try await client.deliver(
                serverAddress: pairing.serverAddress, credential: pairing.credential,
                leaseId: assignment.leaseId, file: candidate,
                sourceSha256: sourceSha256, candidateSha256: candidateSha256)
            progress(receipt.bytes, receipt.bytes)
            return receipt
        }

        let size = (try? FileManager.default.attributesOfItem(atPath: candidate.path)[.size] as? NSNumber)?.int64Value ?? 0
        let handle = try FileHandle(forReadingFrom: candidate)
        defer { try? handle.close() }

        var failures = 0
        while offset < size {
            try Task.checkCancellation()
            try handle.seek(toOffset: UInt64(offset))
            let length = Int(min(chunkBytes, size - offset))
            guard let chunk = try handle.read(upToCount: length), !chunk.isEmpty else {
                throw SidecarError.transferFailed(reason: "The candidate could not be read at offset \(offset).")
            }
            do {
                offset = try await client.uploadChunk(
                    serverAddress: pairing.serverAddress, credential: pairing.credential,
                    leaseId: assignment.leaseId, offset: offset, chunk: chunk)
                progress(offset, size)
            } catch let SidecarError.uploadOffsetMismatch(serverHolds) {
                // The server is the authority on what arrived; carry on from its number.
                offset = serverHolds
            } catch SidecarError.transferFailed {
                failures += 1
                guard failures <= Self.maxChunkFailures else {
                    throw SidecarError.transferFailed(reason: "The candidate upload failed \(failures) times.")
                }
                try await sleep(min(30, Double(failures) * 2))
                offset = try await client.uploadOffset(
                    serverAddress: pairing.serverAddress, credential: pairing.credential, leaseId: assignment.leaseId) ?? offset
            }
        }

        return try await client.completeUpload(
            serverAddress: pairing.serverAddress, credential: pairing.credential,
            leaseId: assignment.leaseId, sourceSha256: sourceSha256, candidateSha256: candidateSha256)
    }

    /// Runs each of the server's measurement commands and reads back its log. Nil means the
    /// measurement could not be made in full — a refused command, a failed ffmpeg, a missing log —
    /// and nothing is reported, so the server never sees half an answer.
    private func measure(
        _ assignment: Assignment, ffmpeg: URL, source: URL, candidate: URL, scratch: URL
    ) async -> [String]? {
        var logs: [String] = []
        let commands = assignment.quality.commands.compactMap { try? MeasurementCommand.validate($0) }
        guard commands.count == assignment.quality.commands.count else { return nil }
        // A sampled window pairs pictures by timestamp, so the server wants the candidate's extra
        // lead over the source removed first. Only this machine has both files to measure it from.
        var distortedShift: String?
        if commands.contains(where: \.needsDistortedShift) {
            guard let ffprobe,
                  let sourceLead = await TimelineLead.measure(ffprobe: ffprobe, file: source, runner: leadProbe),
                  let candidateLead = await TimelineLead.measure(ffprobe: ffprobe, file: candidate, runner: leadProbe)
            else { return nil }
            distortedShift = TimelineLead.shift(candidate: candidateLead, source: sourceLead)
        }
        for (index, command) in commands.enumerated() {
            let log = scratch.appendingPathComponent("vmaf-\(index).json", isDirectory: false)
            let materialised = command.materialise(
                distorted: candidate, reference: source, log: log, distortedShift: distortedShift)
            guard let result = try? await runner.run(ffmpeg, materialised, progress: { _ in }), result.exitCode == 0,
                  let contents = try? String(contentsOf: log, encoding: .utf8), !contents.isEmpty
            else { return nil }
            logs.append(contents)
        }
        return logs
    }

    private func run(
        _ assignment: Assignment,
        pairing: StoredPairing,
        scratch: URL,
        progress: @escaping @Sendable (JobProgress) -> Void,
        preview: @escaping @Sendable (Data) -> Void
    ) async throws -> JobOutcome {
        guard let ffmpeg else {
            return await release(assignment, pairing: pairing, reason: "This build has no ffmpeg to run.")
        }

        let command: AssignmentCommand
        do {
            command = try AssignmentCommand.validate(
                assignment.arguments, outputExtension: assignment.outputExtension)
        } catch let error as AssignmentCommandError {
            return await release(assignment, pairing: pairing, reason: "The server's command was refused: \(error).")
        }

        try FileManager.default.createDirectory(at: scratch, withIntermediateDirectories: true)
        let required = assignment.sourceBytes.addingReportingOverflow(assignment.sourceBytes / 2)
        guard assignment.sourceBytes > 0, !required.overflow else {
            return await release(assignment, pairing: pairing, reason: "The assignment named an invalid source size.")
        }
        let requiredScratch = required.partialValue
        guard let available = availableScratchBytes(scratch) else {
            return await release(
                assignment, pairing: pairing,
                reason: "The Mac could not determine how much scratch space is available, so it did not download the source.")
        }
        guard available >= requiredScratch else {
            return await release(
                assignment, pairing: pairing,
                reason: "The job needs \(requiredScratch) bytes of scratch space, but only \(available) bytes are available.")
        }
        let source = scratch.appendingPathComponent("source", isDirectory: false)
        let candidate = scratch.appendingPathComponent("candidate.\(assignment.outputExtension)", isDirectory: false)
        let latest = LatestProgress(.fetchingSource(received: 0, total: assignment.sourceBytes))

        progress(.fetchingSource(received: 0, total: assignment.sourceBytes))
        let declaredSourceHash = try await whileRenewingLease(
            assignment, pairing: pairing, progress: latest.get
        ) {
            try await fetchSource(assignment, pairing: pairing, destination: source) { received, total in
                let stage = JobProgress.fetchingSource(received: received, total: total)
                latest.set(stage)
                progress(stage)
            }
        }
        let actualSourceHash = try Self.sha256(of: source)
        guard actualSourceHash.caseInsensitiveCompare(declaredSourceHash) == .orderedSame else {
            return await release(assignment, pairing: pairing,
                reason: "The source did not arrive intact (hash mismatch), so it was not encoded.")
        }

        let arguments = command.materialise(input: source, output: candidate)
        latest.set(.encoding(encodedSeconds: 0))
        let encode = try await whileRenewingLease(
            assignment, pairing: pairing, progress: latest.get
        ) {
            try await runner.run(ffmpeg, arguments) { [previewSampler, wantsPreviews] seconds in
                latest.set(.encoding(encodedSeconds: seconds))
                progress(.encoding(encodedSeconds: seconds))
                // Only while someone has the menu open, and never faster than the sampler's own
                // interval: a frame grab is a whole process, and the encode is the job here.
                guard let previewSampler, wantsPreviews() else { return }
                Task {
                    if let frame = await previewSampler.frame(from: source, atSeconds: seconds) {
                        preview(frame)
                    }
                }
            }
        }

        guard encode.0 == 0 else {
            let detail = encode.1.trimmingCharacters(in: .whitespacesAndNewlines)
            return await release(assignment, pairing: pairing,
                reason: "ffmpeg exited with code \(encode.0)." + (detail.isEmpty ? "" : " \(detail)"))
        }

        let candidateHash = try Self.sha256(of: candidate)

        // The server's measurement, run here and returned as the raw logs. Measuring is the one
        // part of verification this machine may contribute, and it is only an offer: if it
        // cannot be made, the candidate is still delivered and the server measures for itself.
        if assignment.quality.measure, !assignment.quality.commands.isEmpty {
            progress(.measuring)
            latest.set(.measuring)
            let logs = try await whileRenewingLease(
                assignment, pairing: pairing, progress: latest.get
            ) {
                await measure(assignment, ffmpeg: ffmpeg, source: source, candidate: candidate, scratch: scratch)
            }
            if let logs {
                try? await client.reportQuality(
                    serverAddress: pairing.serverAddress, credential: pairing.credential,
                    leaseId: assignment.leaseId, sourceSha256: declaredSourceHash,
                    candidateSha256: candidateHash, logs: logs)
            }
        }

        let candidateBytes = (try? FileManager.default
            .attributesOfItem(atPath: candidate.path)[.size] as? NSNumber)?.int64Value ?? 0
        progress(.delivering(sent: 0, total: candidateBytes))
        latest.set(.delivering(sent: 0, total: candidateBytes))
        let receipt = try await whileRenewingLease(
            assignment, pairing: pairing, progress: latest.get
        ) {
            try await deliver(
                assignment, pairing: pairing, candidate: candidate,
                sourceSha256: declaredSourceHash, candidateSha256: candidateHash
            ) { sent, total in
                let stage = JobProgress.delivering(sent: sent, total: total)
                latest.set(stage)
                progress(stage)
            }
        }
        return .delivered(jobId: receipt.jobId, bytes: receipt.bytes)
    }

    /// Hands the job back. Best effort: if the release itself fails the lease lapses on its own
    /// and the server reclaims the job then, so nothing is stranded either way.
    private func release(_ assignment: Assignment, pairing: StoredPairing, reason: String) async -> JobOutcome {
        try? await client.release(
            serverAddress: pairing.serverAddress, credential: pairing.credential,
            leaseId: assignment.leaseId)
        return .released(jobId: assignment.jobId, reason: reason)
    }

    /// Streams the file through SHA-256 so a multi-gigabyte source is never held in memory.
    static func sha256(of file: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: file)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try handle.read(upToCount: 1 << 20), !chunk.isEmpty {
            hasher.update(data: chunk)
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    private static func describe(_ error: SidecarError) -> String {
        switch error {
        case let .unreachable(description): return "The server could not be reached: \(description)"
        case let .unexpectedResponse(status): return "The server replied unexpectedly (HTTP \(status))."
        case let .remoteWorkersDisabled(reason): return reason
        case let .transferFailed(reason): return reason
        case let .deliveryRefused(reason): return reason
        case let .uploadOffsetMismatch(serverHolds): return "The upload lost its place; the server holds \(serverHolds) bytes."
        default: return "\(error)"
        }
    }
}
