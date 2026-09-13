import Foundation

/// Builds the throwaway ffmpeg command that grabs one frame of the source as a small JPEG.
///
/// The frame comes from the **source**, not from the candidate being written. A partially written
/// MP4 has no index yet and cannot be decoded reliably, whereas the source is complete on this
/// machine's own disk. Seeking it to the encoder's current output position shows the picture the
/// encoder is working on, which is the thing worth watching.
public enum FramePreviewCommand {
    /// `-ss` before `-i` so the seek is a cheap keyframe jump rather than a decode from the top.
    public static func arguments(source: URL, atSeconds seconds: Double, width: Int) -> [String] {
        [
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
        await withCheckedContinuation { continuation in
            let process = Process()
            let pipe = Pipe()
            process.executableURL = executable
            process.arguments = arguments
            process.standardOutput = pipe
            process.standardError = FileHandle.nullDevice
            do {
                try process.run()
            } catch {
                continuation.resume(returning: (-1, Data()))
                return
            }
            // Read before waiting: a frame larger than the pipe buffer would otherwise deadlock.
            let data = pipe.fileHandleForReading.readDataToEndOfFile()
            process.waitUntilExit()
            continuation.resume(returning: (process.terminationStatus, data))
        }
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
    public func frame(from source: URL, atSeconds seconds: Double, now: Date = Date()) async -> Data? {
        if let lastSampled, now.timeIntervalSince(lastSampled) < interval { return nil }
        lastSampled = now
        let result = await runner.run(
            ffmpeg, FramePreviewCommand.arguments(source: source, atSeconds: seconds, width: width))
        guard result.exitCode == 0, !result.output.isEmpty else { return nil }
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
