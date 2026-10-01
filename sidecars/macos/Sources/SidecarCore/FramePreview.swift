import Foundation
import Darwin

/// Builds the throwaway ffmpeg command that grabs one frame of the source as a small JPEG.
///
/// The frame comes from the **source**, not from the candidate being written. A partially written
/// MP4 has no index yet and cannot be decoded reliably, whereas the source is complete on this
/// machine's own disk. Seeking it to the encoder's current output position shows the picture the
/// encoder is working on, which is the thing worth watching.
public enum FramePreviewCommand {
    /// `-ss` before `-i` so the seek is a cheap keyframe jump rather than a decode from the top.
    public static func arguments(source: URL, atSeconds seconds: Double, width: Int, audio: Bool = false) -> [String] {
        if audio {
            return [
                "-hide_banner", "-v", "error", "-nostdin", "-max_alloc", "67108864", "-threads", "1",
                "-ss", String(format: "%.2f", locale: Locale(identifier: "en_US_POSIX"), min(604800, max(0, seconds - 3))),
                "-t", "3", "-i", source.path, "-filter_complex_threads", "1",
                "-filter_complex", "[0:a:0]aresample=48000,showspectrumpic=s=320x96:legend=0:scale=log:fscale=log:color=viridis:mode=combined[spectrum]",
                "-map", "[spectrum]", "-an", "-sn", "-dn", "-frames:v", "1", "-threads:v", "1",
                "-q:v", "8", "-c:v", "mjpeg", "-f", "image2pipe", "-"
            ]
        }
        return [
            "-hide_banner", "-v", "error",
            "-ss", String(format: "%.2f", max(0, seconds)),
            "-i", source.path,
            "-frames:v", "1",
            "-vf", "scale=\(max(16, width)):-2",
            "-f", "mjpeg", "-",
        ]
    }
}

/// Runs a command and hands back its raw standard output, which a JPEG needs and a `String` would
/// corrupt.
public protocol BinaryCommandRunner: Sendable {
    func run(_ executable: URL, _ arguments: [String]) async -> (exitCode: Int32, output: Data)
}

public struct ProcessBinaryCommandRunner: BinaryCommandRunner {
    public init() {}

    public func run(_ executable: URL, _ arguments: [String]) async -> (exitCode: Int32, output: Data) {
        let child = PreviewChild()
        return await withTaskCancellationHandler {
            await withCheckedContinuation { continuation in
                DispatchQueue.global(qos: .utility).async {
                    let process = Process()
                    let pipe = Pipe()
                    pipe.sealFromOtherChildren()
                    process.executableURL = executable
                    process.arguments = arguments
                    process.standardOutput = pipe
                    process.standardError = FileHandle.nullDevice
                    do {
                        guard try child.start(process) else {
                            continuation.resume(returning: (-1, Data()))
                            return
                        }
                        let deadline = DispatchWorkItem { child.stop() }
                        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 3, execute: deadline)
                        defer { deadline.cancel() }
                        var data = Data()
                        while let chunk = try pipe.fileHandleForReading.read(upToCount: 4096), !chunk.isEmpty {
                            guard data.count + chunk.count <= 8192 else {
                                child.stop()
                                process.waitUntilExit()
                                continuation.resume(returning: (-1, Data()))
                                return
                            }
                            data.append(chunk)
                        }
                        process.waitUntilExit()
                        continuation.resume(returning: (process.terminationStatus, data))
                    } catch {
                        child.stop()
                        if process.isRunning { process.waitUntilExit() }
                        continuation.resume(returning: (-1, Data()))
                    }
                }
            }
        } onCancel: { child.stop() }
    }
}

/// Process lifetime is shared with deadline and cancellation handlers; both fail closed before start.
private final class PreviewChild: @unchecked Sendable {
    private let lock = NSLock()
    private var process: Process?
    private var stopped = false

    func start(_ child: Process) throws -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard !stopped else { return false }
        try child.run()
        process = child
        return true
    }

    func stop() {
        lock.lock()
        defer { lock.unlock() }
        stopped = true
        if let process, process.isRunning { Darwin.kill(process.processIdentifier, SIGKILL) }
    }
}

/// Grabs preview frames, on demand and never faster than its own interval.
///
/// Rate limiting lives here rather than at the call site because the encoder reports progress many
/// times a second and a frame grab is a whole process launch. Nothing is sampled unless something
/// is actually watching, which the caller decides.
public actor FramePreviewSampler {
    private let ffmpeg: URL
    private let runner: BinaryCommandRunner
    private let interval: TimeInterval
    private let width: Int
    private var lastSampled: Date?
    private var inFlight = false

    public init(
        ffmpeg: URL,
        runner: BinaryCommandRunner = ProcessBinaryCommandRunner(),
        interval: TimeInterval = 1.5,
        width: Int = 320
    ) {
        self.ffmpeg = ffmpeg
        self.runner = runner
        self.interval = interval
        self.width = width
    }

    /// A JPEG of the source at that position, or nil when it is too soon to sample again or the
    /// grab failed. A failure is silent on purpose: a missing preview must never disturb a job.
    public func frame(from source: URL, atSeconds seconds: Double, now: Date = Date(), audio: Bool = false) async -> Data? {
        guard seconds.isFinite, !Task.isCancelled, !inFlight else { return nil }
        inFlight = true
        defer { inFlight = false }
        if let lastSampled, now.timeIntervalSince(lastSampled) < interval { return nil }
        lastSampled = now
        let result = await runner.run(
            ffmpeg, FramePreviewCommand.arguments(source: source, atSeconds: seconds, width: width, audio: audio))
        guard !Task.isCancelled, result.exitCode == 0, !result.output.isEmpty, result.output.count <= 8192 else { return nil }
        return result.output
    }
}

/// A bounded, ordered run of the frames a job has been seen encoding.
///
/// Kept as a strip rather than a single still because one picture every second or so is not much
/// to look at, while a run of them played back is a time-lapse of the encode: it shows the film
/// moving, and it shows at a glance that work is actually progressing.
public struct FilmStrip: Sendable, Equatable {
    /// Enough for a few seconds of playback without holding megabytes of JPEG per job.
    public static let capacity = 24

    public private(set) var frames: [Data] = []

    public init() {}

    public init(frames: [Data]) {
        self.frames = Array(frames.suffix(Self.capacity))
    }

    /// Oldest frames fall off the front, so the strip is always the most recent run.
    public mutating func append(_ frame: Data) {
        frames.append(frame)
        if frames.count > Self.capacity {
            frames.removeFirst(frames.count - Self.capacity)
        }
    }

    public var isEmpty: Bool { frames.isEmpty }

    /// The frame to show at a given tick of playback, cycling. Nil while the strip is empty.
    public func frame(atTick tick: Int) -> Data? {
        guard !frames.isEmpty else { return nil }
        return frames[((tick % frames.count) + frames.count) % frames.count]
    }
}

/// Whether anything is currently watching for preview frames.
///
/// A shared box rather than a flag on the session because the runner is built before the session
/// exists and reads this from ffmpeg's progress callback, which is neither the main actor nor a
/// place to reach back into the UI.
public final class PreviewGate: @unchecked Sendable {
    private let lock = NSLock()
    private var wanted = false

    public init() {}

    public var isWanted: Bool { lock.withLock { wanted } }

    public func set(_ wanted: Bool) { lock.withLock { self.wanted = wanted } }
}
