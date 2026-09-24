import CryptoKit
import Foundation
import os

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

    func run(
        _ executable: URL,
        _ arguments: [String],
        sizeBudget: OutputSizeBudget?,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> (exitCode: Int32, stderr: String)
}

public struct OutputSizeBudget: Sendable, Equatable {
    public let output: URL
    public let maxBytes: Int64
    public init(output: URL, maxBytes: Int64) { self.output = output; self.maxBytes = maxBytes }
}

public struct OutputSizeExceeded: Error, Sendable, Equatable {
    public let observedBytes: Int64
    public let maxBytes: Int64
}

public extension TranscodeRunner {
    func run(
        _ executable: URL, _ arguments: [String], sizeBudget: OutputSizeBudget?,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> (exitCode: Int32, stderr: String) {
        try await run(executable, arguments, progress: progress)
    }
}

public struct ProcessTranscodeRunner: TranscodeRunner {
    public init() {}

    /// How long the pipes are given to hand over their last bytes once the process itself has
    /// gone. Reached only when something other than the child still holds a write end; ordinarily
    /// both reach end-of-file within a scheduling tick of the exit.
    static let drainGrace: TimeInterval = 2

    /// Process and its pipes are thread-safe Foundation objects that the compiler cannot see as
    /// such; the box lets the cancellation handler reach the process to terminate it, and holds
    /// the one question — is this run answered yet — that several callbacks race to decide.
    ///
    /// The answer is given when the process has exited *and* either both pipes have reached
    /// end-of-file or the grace has run out. The exit is the authority. Waiting for the pipes
    /// instead is what wedged a Mac in "Measuring" for thirty-six minutes with no FFmpeg running
    /// at all, its lease renewing perfectly underneath it, because a descriptor for the stdout
    /// pipe had left with some other child and end-of-file was never coming.
    private final class RunningProcess: @unchecked Sendable {
        typealias Answer = (Result<(exitCode: Int32, stderr: String), Error>) -> Void

        let process = Process()
        let stdout = Pipe()
        let stderr = Pipe()

        private let gate = NSLock()
        private var tail = Data()
        private var partialLine = ""
        private var openPipes = 2
        private var exitStatus: Int32?
        private var graceElapsed = false
        private var answer: Answer?
        private var answered = false

        /// Splits FFmpeg's `-progress` stream into lines across reads, since a read boundary falls
        /// wherever the pipe happens to fill.
        func progressSeconds(in data: Data) -> [Double] {
            gate.lock()
            partialLine += String(decoding: data, as: UTF8.self)
            var seconds: [Double] = []
            while let newline = partialLine.firstIndex(of: "\n") {
                let line = String(partialLine[partialLine.startIndex..<newline])
                partialLine = String(partialLine[partialLine.index(after: newline)...])
                if let value = FfmpegProgressLine.encodedSeconds(line) { seconds.append(value) }
            }
            // A `-progress` line is a short `key=value`. Anything that arrives with no newline at
            // all is not that, and must not be allowed to grow for the length of an encode.
            if partialLine.count > 64 * 1024 { partialLine = "" }
            gate.unlock()
            return seconds
        }

        /// Keeps the end of FFmpeg's diagnostics. The whole of it runs to megabytes on a long
        /// encode, and only the end of it ever says why one failed.
        func keep(_ data: Data) {
            gate.lock()
            tail.append(data)
            if tail.count > 64 * 1024 { tail.removeFirst(tail.count - 64 * 1024) }
            gate.unlock()
        }

        func pipeReachedEnd() {
            gate.lock(); openPipes -= 1; gate.unlock()
            answerIfReady()
        }

        func exited(_ status: Int32) {
            gate.lock(); exitStatus = status; gate.unlock()
            answerIfReady()
        }

        func graceRanOut() {
            gate.lock(); graceElapsed = true; gate.unlock()
            answerIfReady()
        }

        func waitFor(_ answer: @escaping Answer) {
            gate.lock(); self.answer = answer; gate.unlock()
            answerIfReady()
        }

        /// Lets go of the pipes when there will be no process to read from. Without this a
        /// command that would not launch leaves two readability handlers holding this object,
        /// and its four descriptors, for the life of the app.
        func abandon() {
            stdout.fileHandleForReading.readabilityHandler = nil
            stderr.fileHandleForReading.readabilityHandler = nil
        }

        private func answerIfReady() {
            gate.lock()
            guard !answered, let status = exitStatus, let answer,
                  openPipes == 0 || graceElapsed
            else { gate.unlock(); return }
            answered = true
            self.answer = nil
            let text = String(decoding: tail.suffix(4_096), as: UTF8.self)
            gate.unlock()

            stdout.fileHandleForReading.readabilityHandler = nil
            stderr.fileHandleForReading.readabilityHandler = nil
            answer(.success((status, text)))
        }
    }

    public func run(
        _ executable: URL,
        _ arguments: [String],
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> (exitCode: Int32, stderr: String) {
        try await run(executable, arguments, sizeBudget: nil, progress: progress)
    }

    public func run(
        _ executable: URL,
        _ arguments: [String],
        sizeBudget: OutputSizeBudget?,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> (exitCode: Int32, stderr: String) {
        let running = RunningProcess()
        running.process.executableURL = executable
        running.process.arguments = arguments
        running.process.standardOutput = running.stdout
        running.process.standardError = running.stderr
        running.stdout.sealFromOtherChildren()
        running.stderr.sealFromOtherChildren()

        // Read as it arrives rather than to end-of-file, so neither pipe can decide when the job
        // is over.
        running.stdout.fileHandleForReading.readabilityHandler = { handle in
            let data = handle.availableData
            guard !data.isEmpty else {
                handle.readabilityHandler = nil
                running.pipeReachedEnd()
                return
            }
            for seconds in running.progressSeconds(in: data) { progress(seconds) }
        }
        running.stderr.fileHandleForReading.readabilityHandler = { handle in
            let data = handle.availableData
            guard !data.isEmpty else {
                handle.readabilityHandler = nil
                running.pipeReachedEnd()
                return
            }
            running.keep(data)
        }

        // Set before the process starts: a command that fails immediately can be gone before
        // there is anything listening for it.
        running.process.terminationHandler = { process in
            running.exited(process.terminationStatus)
            // The pipes are given a moment to hand over what they still hold, because the tail of
            // stderr is the only account of why an encode failed. They are not given a veto.
            DispatchQueue.global().asyncAfter(deadline: .now() + Self.drainGrace) {
                running.graceRanOut()
            }
        }

        do {
            try running.process.run()
        } catch {
            running.abandon()
            throw error
        }

        let budgetWatch: Task<Int64?, Never>? = sizeBudget.map { budget in
            Task {
                while !Task.isCancelled {
                    try? await Task.sleep(for: .seconds(2))
                    if Task.isCancelled { return nil }
                    guard running.process.isRunning,
                          let number = try? FileManager.default.attributesOfItem(atPath: budget.output.path)[.size] as? NSNumber,
                          number.int64Value > budget.maxBytes else { continue }
                    running.process.terminate()
                    return number.int64Value
                }
                return nil
            }
        }
        defer { budgetWatch?.cancel() }
        let result = try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                running.waitFor { continuation.resume(with: $0) }
            }
        } onCancel: {
            running.process.terminate()
        }
        budgetWatch?.cancel()
        if let observed = await budgetWatch?.value, let sizeBudget {
            throw OutputSizeExceeded(observedBytes: observed, maxBytes: sizeBudget.maxBytes)
        }
        return result
    }
}

/// How one assignment ended, in terms the menu can show and a log can keep.
public enum JobOutcome: Sendable, Equatable {
    /// The candidate reached the server intact. Verification there decides what it is worth.
    case delivered(jobId: Int, bytes: Int64)

    /// The server accepted a terminal failure for this attempt; it will not requeue it.
    case failed(jobId: Int, reason: String)

    /// The job was handed back for the server to reassign, with the reason it could not be done.
    case released(jobId: Int, reason: String)

    /// The hand-back could not be confirmed; a shutdown must wait rather than assume the lease ended.
    case unconfirmed(jobId: Int, reason: String)

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

public extension JobProgress {
    /// One word for the stage, for the menu's header line.
    var summary: String {
        switch self {
        case .fetchingSource: return "Receiving"
        case .encoding: return "Encoding"
        case .measuring: return "Measuring"
        case .delivering: return "Sending"
        }
    }

    /// Bytes moved so far, for the stages that move any.
    var transferredBytes: Int64? {
        switch self {
        case let .fetchingSource(received, _): return received
        case let .delivering(sent, _): return sent
        case .encoding, .measuring: return nil
        }
    }

    /// Whether two reports describe the same stage, ignoring how far through it they are.
    func isSameStage(as other: JobProgress) -> Bool {
        switch (self, other) {
        case (.fetchingSource, .fetchingSource), (.encoding, .encoding),
             (.measuring, .measuring), (.delivering, .delivering):
            return true
        default:
            return false
        }
    }
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
    /// A thread-safe box rather than a closure, and deliberately so.
    ///
    /// These are read here, on a background task, while the settings themselves live on the main
    /// actor for the options panel to observe. A closure invites bridging that with
    /// `MainActor.assumeIsolated`, which traps rather than blocks; that shipped in 0.1.5 and
    /// crashed the app the moment a job was claimed. A box cannot be misused that way.
    ///
    /// Read per job, so changing a setting takes effect on the next job without a restart.
    private let settings: SettingsSnapshot
    private let availableScratchBytes: @Sendable (URL) -> Int64?
    private let sleep: @Sendable (TimeInterval) async throws -> Void
    /// Read on each lease renewal, so the load shown beside a running job is current rather than
    /// up to a check-in interval old. Its own sampler: see `MachineLoadSampler`.
    private let load: MachineLoadSampler

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
        load: MachineLoadSampler = MachineLoadSampler(),
        settings: SettingsSnapshot = SettingsSnapshot(
            workLocation: .applicationSupport,
            memoryBudgetBytes: WorkLocationPolicy.defaultBudget(
                physicalBytes: Int64(ProcessInfo.processInfo.physicalMemory))),
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
        self.settings = settings
        self.availableScratchBytes = availableScratchBytes
        self.sleep = sleep
        self.load = load
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
        // Where this job works, which the operator may have moved to another volume or to memory.
        // A RAM disk is made per job and sized to it, so an idle sidecar holds no memory at all,
        // and a job too large for the memory budget quietly runs on disk instead of being lost.
        let required = assignment.sourceBytes + assignment.sourceBytes / 2
        let preference = settings.workLocation
        let budget = settings.memoryBudgetBytes
        // Logged before anything is created, so a step that never returns is visible as a job that
        // started and said nothing more, rather than as a worker that silently stopped asking.
        SidecarLog.job.info("""
            Job \(assignment.jobId) claimed: \(assignment.videoEncoder, privacy: .public), \
            source \(assignment.sourceBytes) bytes, needs \(required) bytes, \
            wants \(String(describing: preference), privacy: .public)
            """)
        var ramDisk: RamDisk?
        let root: URL
        switch WorkLocationPolicy.resolve(
            preference: preference, requiredBytes: required, memoryBudget: budget
        ) {
        case .applicationSupport:
            root = scratchRoot
        case let .folder(folder):
            root = folder
        case .memory:
            SidecarLog.storage.info("Job \(assignment.jobId): creating a \(required)-byte RAM disk")
            if let disk = RamDisk.create(bytes: required) {
                ramDisk = disk
                root = disk.mountPoint
                SidecarLog.storage.info(
                    "Job \(assignment.jobId): working in memory on \(disk.mountPoint.path, privacy: .public)")
            } else {
                // The Mac would not give us the volume. That is a reason to use the disk, not a
                // reason to give the job back.
                root = scratchRoot
                SidecarLog.storage.error("Job \(assignment.jobId): no RAM disk could be created; using the disk")
            }
        }
        if let reason = WorkLocationPolicy.fallbackReason(
            preference: preference, requiredBytes: required, memoryBudget: budget) {
            SidecarLog.storage.notice("Job \(assignment.jobId): \(reason, privacy: .public)")
        }
        SidecarLog.job.info("""
            Job \(assignment.jobId) starting: encoder \(assignment.videoEncoder, privacy: .public), \
            source \(assignment.sourceBytes) bytes, working in \(root.path, privacy: .public)
            """)

        let scratch = root.appendingPathComponent("lease-\(assignment.leaseId)", isDirectory: true)
        defer {
            try? FileManager.default.removeItem(at: scratch)
            // Before anything else can go wrong: a RAM disk that outlives its job holds real
            // memory until the Mac reboots, and nothing on screen would say so.
            ramDisk?.destroy()
        }

        do {
            let outcome = try await run(
                assignment, pairing: pairing, scratch: scratch, progress: progress, preview: preview)
            SidecarLog.job.info("Job \(assignment.jobId) finished: \(String(describing: outcome), privacy: .public)")
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
        let load = self.load
        return try await withThrowingTaskGroup(of: LeaseOperation<T>.self) { group in
            group.addTask { .completed(try await operation()) }
            group.addTask {
                let interval = min(15, max(5, Double(assignment.renewWithinSeconds) / 2))
                // The lease is only really gone when the server says so, or when it has been out
                // of touch for longer than the window the lease was granted for. A single failed
                // renewal used to end this loop and take the job with it, so a restarted
                // container — a deployment, which happens often — threw away every encode that
                // was running at the time, minutes in.
                let window = Double(assignment.renewWithinSeconds)
                var lastRenewed = Date()
                while !Task.isCancelled {
                    try await sleep(interval)
                    do {
                        try await client.renew(
                            serverAddress: pairing.serverAddress, credential: pairing.credential,
                            leaseId: assignment.leaseId, progress: progress(), load: load.sample())
                        lastRenewed = Date()
                        continue
                    } catch let problem as SidecarError where problem.endsTheLease {
                        // The server has said this job is not this worker's. Thrown rather than
                        // returned, so the job ends as a lost lease and not as something this
                        // machine chose to hand back.
                        throw problem
                    } catch {
                        // Every other reason is the same reason: a renewal that did not happen.
                        // What stopped it says nothing about whether the lease survives; only how
                        // long it has been since one landed does.
                    }

                    guard Date().timeIntervalSince(lastRenewed) < window else {
                        throw SidecarError.leaseLost(reason: """
                            The lease could not be renewed for \(Int(window)) seconds, \
                            which is as long as the server granted it for.
                            """)
                    }
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
            } catch let problem as SidecarError where problem.endsTheLease {
                throw problem
            } catch {
                // Anything that is not the server saying this lease is finished with. It used to
                // be only `transferFailed`, so a 502 from a proxy in front of a restarting
                // container arrived as `unexpectedResponse`, went uncaught, and threw away a
                // complete encode that had already been measured and accepted. Delivery is
                // resumable — the server says how much it holds — so a blink costs a pause.
                failures += 1
                guard failures <= Self.maxChunkFailures else {
                    throw SidecarError.transferFailed(reason: "The candidate upload failed \(failures) times.")
                }
                try await sleep(min(30, Double(failures) * 2))
                // Asked again rather than assumed: the server is the authority on what arrived,
                // and starting from the wrong number sends gigabytes twice or leaves a hole.
                if let held = try? await client.uploadOffset(
                    serverAddress: pairing.serverAddress, credential: pairing.credential,
                    leaseId: assignment.leaseId) {
                    offset = held
                }
            }
        }

        // The last call of the whole job, and the one worth repeating most: every byte is already
        // on the server and only the word that says so is missing.
        while true {
            try Task.checkCancellation()
            do {
                return try await client.completeUpload(
                    serverAddress: pairing.serverAddress, credential: pairing.credential,
                    leaseId: assignment.leaseId, sourceSha256: sourceSha256, candidateSha256: candidateSha256)
            } catch let problem as SidecarError where problem.endsTheLease || problem.isRefusal {
                throw problem
            } catch {
                failures += 1
                guard failures <= Self.maxChunkFailures else { throw error }
                try await sleep(min(30, Double(failures) * 2))
            }
        }
    }

    /// What a completed search left behind: the encode to run, or why there is none.
    private enum SearchOutcome {
        case settled(AssignmentCommand)
        case failed(reason: String)
    }

    /// Measures each candidate the server asks for, until it stops asking.
    ///
    /// <br>Bounded by the server: it sends at most four candidates and then names one. This loop
    /// ends when it is told to, or when a measurement cannot be made — in which case the job goes
    /// back rather than being encoded at a quality nobody chose. Encoding at the assignment's
    /// baseline instead would silently discard the search and produce a file the library did not
    /// ask for.
    private func runSearch(
        _ first: AdaptiveSearchStep,
        _ assignment: Assignment,
        pairing: StoredPairing,
        ffmpeg: URL,
        source: URL,
        scratch: URL,
        latest: LatestProgress,
        progress: @escaping @Sendable (JobProgress) -> Void,
        preview: @escaping @Sendable (Data) -> Void
    ) async throws -> SearchOutcome {
        var step = first
        // The server bounds its own search at four candidates, but this machine should not depend
        // on that to stop: a bound only the other end enforces is not a bound. Twice the expected
        // number leaves ordinary searches untouched and still ends a conversation that has stopped
        // making sense.
        let maximumCandidates = 8
        var measured = 0

        while true {
            measured += 1
            guard measured <= maximumCandidates else {
                return .failed(reason:
                    "The search did not settle after \(maximumCandidates) candidates, so no quality was chosen.")
            }
            latest.set(.measuring)
            progress(.measuring)
            SidecarLog.job.info(
                "Job \(assignment.jobId): measuring quality \(step.quality, privacy: .public)")

            // Renewed throughout, like every other stage. It was not, and a search is the longest
            // thing this machine does before it has anything to show: several sample encodes and a
            // VMAF pass each, minutes of them, while the server heard nothing. The lease lapsed,
            // the job went back on the queue, and the next holder started the search again from
            // the beginning — which is what the expired lease on the first search ever run here
            // records.
            let candidate: CandidateMeasurement
            do {
                candidate = try await whileRenewingLease(
                    assignment, pairing: pairing, progress: latest.get
                ) { [step] in
                    await self.measureCandidate(
                        step, assignment, ffmpeg: ffmpeg, source: source, scratch: scratch,
                        preview: preview)
                }
            } catch let problem as SidecarError where problem.endsTheLease {
                throw problem
            } catch {
                return .failed(reason:
                    "The lease could not be renewed while a candidate was being measured.")
            }

            guard case let .measured(windowBytes, logs) = candidate else {
                guard case let .failed(reason) = candidate else { return .failed(reason: "unreachable") }
                SidecarLog.job.error(
                    "Job \(assignment.jobId): \(reason, privacy: .public)")
                return .failed(reason: "The quality search stopped: \(reason).")
            }

            let direction: AdaptiveSearchDirection
            do {
                direction = try await client.reportAdaptiveProbe(
                    serverAddress: pairing.serverAddress, credential: pairing.credential,
                    leaseId: assignment.leaseId,
                    quality: step.quality, windowEncodedBytes: windowBytes, logs: logs)
            } catch let problem as SidecarError where problem.endsTheLease {
                throw problem
            } catch {
                return .failed(reason: "The measurement could not be reported: \(error).")
            }

            if let next = direction.nextStep {
                step = next
                continue
            }

            // The search is over. The arguments that come back name the chosen quality; the ones
            // the assignment arrived with were built before any quality existed.
            guard let settled = direction.arguments,
                  let rebuilt = try? AssignmentCommand.validate(
                      settled, outputExtension: assignment.outputExtension)
            else {
                return .failed(reason:
                    "The server chose a quality but sent no usable command to encode with.")
            }

            SidecarLog.job.info("""
                Job \(assignment.jobId): search chose quality \
                \(direction.selectedQuality.map(String.init) ?? "?", privacy: .public)
                """)
            return .settled(rebuilt)
        }
    }

    /// Encodes one candidate's sample windows and scores each, returning the bytes and raw logs.
    ///
    /// Nil when any part of it could not be done. A partial answer would be worse than none: the
    /// server pools the windows into one score, and a missing window is a different measurement
    /// rather than a smaller one.
    /// What measuring one candidate produced, or why it produced nothing.
    ///
    /// The reason is the point. Every failure below used to be a bare `nil`, which the search
    /// turned into "a sample encode or its measurement could not be completed" — one sentence for
    /// a refused command, an ffmpeg that would not start, one that failed, a sample that encoded
    /// to nothing, and a missing log. None of them named the window, the quality, or a word of
    /// what ffmpeg said, and the scratch directory is deleted on the way out, so afterwards there
    /// is nothing left to look at.
    enum CandidateMeasurement {
        /// One byte count per sample window, in command order, so the server's size forecast can
        /// compare each with the source's own bytes over the same scenes.
        case measured(windowBytes: [Int64], logs: [String])
        case failed(String)
    }

    /// The last few distinct lines of ffmpeg's error output, which is where it says what it
    /// actually objected to. Distinct because a filter error repeats the same line per stream.
    static func tail(_ errorTail: String, lines: Int = 4) -> String {
        var seen: [String] = []
        for line in errorTail.split(whereSeparator: \.isNewline).reversed() {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed.isEmpty || seen.contains(trimmed) { continue }
            seen.append(trimmed)
            if seen.count == lines { break }
        }
        return seen.reversed().joined(separator: " ")
    }

    private func measureCandidate(
        _ step: AdaptiveSearchStep,
        _ assignment: Assignment,
        ffmpeg: URL,
        source: URL,
        scratch: URL,
        preview: @escaping @Sendable (Data) -> Void
    ) async -> CandidateMeasurement {
        // Each refusal named, not counted. Both of these were `compactMap` into a count check, so
        // a command the server sent that this machine would not run became "a sample encode or its
        // measurement could not be completed" — one sentence for five different problems, none of
        // them saying which command, which window, or a word of what ffmpeg said. The Mac spent an
        // afternoon handing every job back with that line while the answer sat in a filter graph
        // nobody could see.
        var commands: [AssignmentCommand] = []
        for (index, argv) in step.sampleCommands.enumerated() {
            do {
                commands.append(
                    try AssignmentCommand.validate(argv, outputExtension: assignment.outputExtension))
            } catch {
                return .failed("""
                    the sample encode for window \(index) at quality \(step.quality) was refused: \(error)
                    """)
            }
        }

        var measurements: [MeasurementCommand] = []
        for (index, argv) in step.measurement.commands.enumerated() {
            do {
                measurements.append(try MeasurementCommand.validate(argv))
            } catch {
                return .failed("""
                    the command to score window \(index) at quality \(step.quality) was refused: \(error)
                    """)
            }
        }

        guard measurements.count == commands.count else {
            return .failed("""
                the server sent \(commands.count) sample encode(s) and \(measurements.count) \
                command(s) to score them with
                """)
        }

        var windowBytes: [Int64] = []
        var logs: [String] = []

        for (index, sample) in commands.enumerated() {
            // Named with the contract's extension, as the encode's own output is: the extension
            // chooses the muxer, so it is part of the command rather than a local convention.
            let encoded = scratch
                .appendingPathComponent("sample-q\(step.quality)-\(index)", isDirectory: false)
                .appendingPathExtension(assignment.outputExtension)

            // A picture from each window as it is sampled. The search is now most of what a job
            // spends its time on — four candidates across three windows — and the film strip sat
            // empty for all of it, because frames were only ever grabbed during the final encode.
            // The window's own start is used rather than the encoder's position: a sample is forty
            // seconds cut from the middle of a title, so its own clock says nothing about where in
            // the film it came from.
            let windowStart = step.measurement.sampling
            guard let run = try? await runner.run(
                ffmpeg, sample.materialise(input: source, output: encoded),
                progress: { [previewSampler, wantsPreviews] _ in
                    guard let previewSampler, wantsPreviews() else { return }
                    Task {
                        if let frame = await previewSampler.frame(
                            from: source, atSeconds: Self.seconds(intoWindow: index, of: windowStart)) {
                            preview(frame)
                        }
                    }
                })
            else {
                return .failed("sample \(index + 1) at quality \(step.quality) could not be started")
            }

            guard run.exitCode == 0 else {
                return .failed("""
                    sample \(index + 1) at quality \(step.quality) would not encode \
                    (ffmpeg exit \(run.exitCode)): \(Self.tail(run.stderr))
                    """)
            }

            guard let size = try? FileManager.default
                .attributesOfItem(atPath: encoded.path)[.size] as? Int64, size > 0
            else {
                return .failed("sample \(index + 1) at quality \(step.quality) encoded to nothing at all")
            }

            windowBytes.append(size)

            // A sample begins at its own first picture, so there is no lead to remove — unlike a
            // finished candidate, where the window is a slice of a whole file.
            let log = scratch.appendingPathComponent("sample-vmaf-q\(step.quality)-\(index).json", isDirectory: false)
            guard let scored = try? await runner.run(
                ffmpeg,
                measurements[index].materialise(
                    distorted: encoded, reference: source, log: log, distortedShift: nil),
                progress: { _ in })
            else {
                return .failed("scoring sample \(index + 1) at quality \(step.quality) could not be started")
            }

            guard scored.exitCode == 0 else {
                return .failed("""
                    sample \(index + 1) at quality \(step.quality) would not score \
                    (ffmpeg exit \(scored.exitCode)): \(Self.tail(scored.stderr))
                    """)
            }

            guard let text = try? String(contentsOf: log, encoding: .utf8), !text.isEmpty else {
                return .failed("""
                    scoring sample \(index + 1) at quality \(step.quality) wrote no libvmaf log
                    """)
            }

            logs.append(text)

            // Removed as it goes: four candidates across three windows is a dozen sample encodes,
            // and keeping them all would need as much scratch again as the job itself.
            try? FileManager.default.removeItem(at: encoded)
            try? FileManager.default.removeItem(at: log)
        }

        return .measured(windowBytes: windowBytes, logs: logs)
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
        let frameSeconds = commands.contains(where: \.needsDistortedShift)
            ? await TimelineAlignment.frameSeconds(ffprobe: ffprobe, file: source, runner: leadProbe) ?? (1.0 / 25.0)
            : nil

        for (index, command) in commands.enumerated() {
            var distortedShift: String?
            if command.needsDistortedShift {
                guard let frameSeconds,
                      let measured = await TimelineAlignment.measure(
                          ffmpeg: ffmpeg, command: command, source: source, candidate: candidate,
                          frameSeconds: frameSeconds, scratch: scratch, runner: runner)
                else { return nil }
                distortedShift = measured
            }
            SidecarLog.job.info("""
                Job \(assignment.jobId): measuring window \(index + 1)/\(commands.count), \
                distorted shift \(distortedShift ?? "none", privacy: .public)
                """)
            let log = scratch.appendingPathComponent("vmaf-\(index).json", isDirectory: false)
            let materialised = command.materialise(
                distorted: candidate, reference: source, log: log, distortedShift: distortedShift)
            guard let result = try? await runner.run(ffmpeg, materialised, progress: { _ in }), result.exitCode == 0,
                  let contents = try? String(contentsOf: log, encoding: .utf8), !contents.isEmpty
            else {
                SidecarLog.job.error("Job \(assignment.jobId): window \(index) could not be measured")
                return nil
            }
            if let summary = VmafLogSummary.of(contents) {
                SidecarLog.job.info(
                    "Job \(assignment.jobId): window \(index) \(summary, privacy: .public)")
            }
            logs.append(contents)
        }
        return logs
    }

    /// Roughly where in the title a sample window sits, for the preview to seek to.
    ///
    /// The server names the windows only in prose — "Adaptive sample at quality 24" — so this
    /// spreads the grabs across the title rather than pretending to know. It is a picture for a
    /// person to look at, not a measurement, and three stills from three different parts of a film
    /// is exactly what it should show.
    static func seconds(intoWindow index: Int, of sampling: String) -> Double {
        Double(120 + (index * 600))
    }

    /// Says once, per reason, why no picture is appearing.
    ///
    /// Previews were entirely silent: the film strip stayed empty and there was no way to tell a
    /// build with no ffmpeg from a menu nobody had open from a grab that was failing. Once per
    /// reason rather than per frame, because this fires several times a second for the length of
    /// an encode.
    private static let previewSilence = OSAllocatedUnfairLock(initialState: Set<String>())

    static func notePreviewsOff(jobId: Int, because reason: String) {
        let key = "\(jobId):\(reason)"
        let isNew = previewSilence.withLock { seen in seen.insert(key).inserted }
        guard isNew else { return }
        SidecarLog.job.info("Job \(jobId): no frame previews — \(reason, privacy: .public)")
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

        // The per-title quality search, when the server sent one. It runs here, on the encoder that
        // will do the real encode, because a quality proven by measuring one encoder means nothing
        // on another. The server chooses every candidate; this machine measures them.
        var encodeCommand = command
        if let first = assignment.search {
            switch try await runSearch(
                first, assignment, pairing: pairing, ffmpeg: ffmpeg,
                source: source, scratch: scratch, latest: latest, progress: progress,
                preview: preview)
            {
            case .settled(let settled):
                encodeCommand = settled
            case .failed(let reason):
                return await release(assignment, pairing: pairing, reason: reason)
            }
        }

        let arguments = encodeCommand.materialise(input: source, output: candidate)
        latest.set(.encoding(encodedSeconds: 0))
        let encode: (exitCode: Int32, stderr: String)
        do {
            encode = try await whileRenewingLease(
                assignment, pairing: pairing, progress: latest.get
            ) {
                try await runner.run(ffmpeg, arguments,
                    sizeBudget: assignment.maxCandidateBytes.map { OutputSizeBudget(output: candidate, maxBytes: $0) }) {
                    [previewSampler, wantsPreviews] seconds in
                    latest.set(.encoding(encodedSeconds: seconds))
                    progress(.encoding(encodedSeconds: seconds))
                    // Only while someone has the menu open, and never faster than the sampler's own
                    // interval: a frame grab is a whole process, and the encode is the job here.
                    guard let previewSampler else {
                        Self.notePreviewsOff(jobId: assignment.jobId, because: "this build has no ffmpeg to grab one with")
                        return
                    }
                    guard wantsPreviews() else {
                        Self.notePreviewsOff(jobId: assignment.jobId, because: "nothing is watching")
                        return
                    }
                    Task {
                        if let frame = await previewSampler.frame(from: source, atSeconds: seconds) {
                            preview(frame)
                        }
                    }
                }
            }
        } catch let exceeded as OutputSizeExceeded {
            try await client.reportSizeBudgetExceeded(
                serverAddress: pairing.serverAddress, credential: pairing.credential,
                leaseId: assignment.leaseId, observedBytes: exceeded.observedBytes)
            return .failed(jobId: assignment.jobId,
                reason: "Size saving: candidate exceeded the \(exceeded.maxBytes)-byte budget; encoding stopped.")
        }

        guard encode.0 == 0 else {
            let detail = encode.1.trimmingCharacters(in: .whitespacesAndNewlines)
            return await release(assignment, pairing: pairing,
                reason: "ffmpeg exited with code \(encode.0)." + (detail.isEmpty ? "" : " \(detail)"))
        }

        // The final mux can add bytes between the size monitor's last poll and FFmpeg's exit.
        // Report that as the same terminal lease result before hashing or measuring quality.
        if let maximum = assignment.maxCandidateBytes,
           let size = (try? FileManager.default.attributesOfItem(atPath: candidate.path)[.size] as? NSNumber)?.int64Value,
           size > maximum {
            try await client.reportSizeBudgetExceeded(
                serverAddress: pairing.serverAddress, credential: pairing.credential,
                leaseId: assignment.leaseId, observedBytes: size)
            return .failed(jobId: assignment.jobId,
                reason: "Size saving: finished candidate exceeded the \(maximum)-byte budget.")
        }

        if let minimum = assignment.minCandidateBytes,
           let size = (try? FileManager.default.attributesOfItem(atPath: candidate.path)[.size] as? NSNumber)?.int64Value,
           size < minimum {
            try await client.reportSizeBudgetUndershot(
                serverAddress: pairing.serverAddress, credential: pairing.credential,
                leaseId: assignment.leaseId, observedBytes: size)
            return .failed(jobId: assignment.jobId,
                reason: "Compression ceiling: finished candidate was below the \(minimum)-byte floor.")
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

        if let verification = assignment.fullVerification {
            progress(.measuring)
            latest.set(.measuring)
            let evidence = try await whileRenewingLease(assignment, pairing: pairing, progress: latest.get) {
                try await FullVerification(runner: runner).measure(contract: verification, ffmpeg: ffmpeg,
                    ffprobe: ffprobe, source: source, candidate: candidate, scratch: scratch,
                    sourceHash: declaredSourceHash, candidateHash: candidateHash)
            }
            try await client.reportVerification(serverAddress: pairing.serverAddress, credential: pairing.credential,
                leaseId: assignment.leaseId, evidence: evidence)
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
        do {
            try await client.release(
                serverAddress: pairing.serverAddress, credential: pairing.credential,
                leaseId: assignment.leaseId)
            return .released(jobId: assignment.jobId, reason: reason)
        } catch {
            return .unconfirmed(jobId: assignment.jobId, reason: "\(reason) Hand-back was not acknowledged: \(error.localizedDescription)")
        }
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
