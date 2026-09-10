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
public enum JobProgress: Sendable, Equatable {
    case fetchingSource
    case encoding(encodedSeconds: Double)
    case delivering
}

/// Executes assignments, so the session can be driven in tests without an ffmpeg or a server.
public protocol WorkExecutor: Sendable {
    func execute(
        _ assignment: Assignment,
        pairing: StoredPairing,
        progress: @escaping @Sendable (JobProgress) -> Void
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
    private let runner: TranscodeRunner
    private let scratchRoot: URL
    private let sleep: @Sendable (TimeInterval) async throws -> Void

    public init(
        client: SidecarClient = SidecarClient(),
        ffmpeg: URL? = CapabilityProber.bundledFfmpeg(),
        runner: TranscodeRunner = ProcessTranscodeRunner(),
        scratchRoot: URL = JobRunner.defaultScratchRoot(),
        sleep: @escaping @Sendable (TimeInterval) async throws -> Void = { seconds in
            try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
        }
    ) {
        self.client = client
        self.ffmpeg = ffmpeg
        self.runner = runner
        self.scratchRoot = scratchRoot
        self.sleep = sleep
    }

    /// Scratch under the app's own support directory rather than a shared temp location, so a
    /// multi-gigabyte source is somewhere an operator can find, and never in anyone else's way.
    public static func defaultScratchRoot() -> URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        return base.appendingPathComponent("OptimisarrSidecar/work", isDirectory: true)
    }

    public func execute(
        _ assignment: Assignment,
        pairing: StoredPairing,
        progress: @escaping @Sendable (JobProgress) -> Void
    ) async -> JobOutcome {
        let scratch = scratchRoot.appendingPathComponent("lease-\(assignment.leaseId)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: scratch) }

        do {
            let outcome = try await run(assignment, pairing: pairing, scratch: scratch, progress: progress)
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

    private func run(
        _ assignment: Assignment,
        pairing: StoredPairing,
        scratch: URL,
        progress: @escaping @Sendable (JobProgress) -> Void
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
        let source = scratch.appendingPathComponent("source", isDirectory: false)
        let candidate = scratch.appendingPathComponent("candidate.\(assignment.outputExtension)", isDirectory: false)

        progress(.fetchingSource)
        let declaredSourceHash = try await client.fetchSource(
            serverAddress: pairing.serverAddress, credential: pairing.credential,
            leaseId: assignment.leaseId, to: source)
        let actualSourceHash = try Self.sha256(of: source)
        guard actualSourceHash.caseInsensitiveCompare(declaredSourceHash) == .orderedSame else {
            return await release(assignment, pairing: pairing,
                reason: "The source did not arrive intact (hash mismatch), so it was not encoded.")
        }

        let arguments = command.materialise(input: source, output: candidate)
        let encode = try await withThrowingTaskGroup(of: (Int32, String)?.self) { group in
            group.addTask {
                let result = try await runner.run(ffmpeg, arguments) { seconds in
                    progress(.encoding(encodedSeconds: seconds))
                }
                return result
            }
            group.addTask {
                // Renew at half the window the server states, so one missed renewal does not
                // cost the lease. A lost lease throws, which cancels the encode below.
                let interval = max(5, Double(assignment.renewWithinSeconds) / 2)
                while true {
                    try await sleep(interval)
                    try await client.renew(
                        serverAddress: pairing.serverAddress, credential: pairing.credential,
                        leaseId: assignment.leaseId)
                }
            }
            // The encode finishes first in every healthy run; the renewal loop only ever ends by
            // throwing. Either way the other task is cancelled before returning.
            let first = try await group.next()
            group.cancelAll()
            return first ?? nil
        }

        guard let encode, encode.0 == 0 else {
            let detail = encode?.1.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            return await release(assignment, pairing: pairing,
                reason: "ffmpeg exited with code \(encode?.0 ?? -1)." + (detail.isEmpty ? "" : " \(detail)"))
        }

        progress(.delivering)
        let candidateHash = try Self.sha256(of: candidate)
        let receipt = try await client.deliver(
            serverAddress: pairing.serverAddress, credential: pairing.credential,
            leaseId: assignment.leaseId, file: candidate,
            sourceSha256: declaredSourceHash, candidateSha256: candidateHash)
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
        default: return "\(error)"
        }
    }
}
