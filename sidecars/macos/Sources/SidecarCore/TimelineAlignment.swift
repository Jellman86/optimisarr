import Foundation

/// Finds how far the candidate's pictures sit from the source's, by trying.
///
/// The `distortedShift` the server leaves a token for was derived from the container's own
/// account of itself — the video stream's start less the container's. That number is zero for
/// every file either sidecar has ever measured, because the two starts are equal in every real
/// container, so the correction has never once been applied.
///
/// It could not have worked anyway. Two episodes of the same show, encoded by the same command on
/// the same machine, are identical in every header field — container start, stream start, first
/// decoded frame — and yet one needs its candidate moved by a frame and the other is destroyed by
/// the same move:
///
///     S13E20   shift 0 → harmonic  0.21      shift +1 frame → harmonic 92.32
///     S13E21   shift 0 → harmonic 89.88      shift +1 frame → harmonic  0.31
///
/// The difference is not in the headers. It is that frames are still occasionally lost in the
/// encode — one in the first of those files, six in the second — so whether a given window lines
/// up depends on how many went missing before it. No arithmetic over metadata can know that.
///
/// So this measures it instead, with the server's own measurement command cut to a few seconds
/// and run once per candidate offset. The probe must be that command: an earlier one built a graph
/// of its own — no cadence grid, no lead correction, a different seek — and the offset it liked was
/// a frame wrong for the measurement that followed, failing clean encodes at harmonic 9.
public enum TimelineAlignment {
    /// Offsets to try, in frames. One frame either way covers every case seen; a candidate further
    /// out than that is not misaligned, it is a different file.
    static let framesToTry = [0, 1, -1]

    /// Seconds of the window each offset is tried on: long enough to hold motion or a cut, which
    /// is what separates a frame of misalignment from a good match. Two seconds of a static shot
    /// scored every offset alike.
    static let probeSeconds = 5.0

    /// How far another offset must beat the unshifted timeline to be chosen. A frame of real
    /// misalignment costs tens of points; anything closer is the scene, not the timeline.
    static let choiceMarginPoints = 2.0

    /// The shift to hand the server's measurement command, written the way it writes seconds.
    /// Nil when no offset could be scored at all, which the caller treats as it treats any other
    /// measurement it could not make.
    public static func measure(
        ffmpeg: URL,
        command: MeasurementCommand,
        source: URL,
        candidate: URL,
        frameSeconds: Double = 1.0 / 25.0,
        scratch: URL,
        runner: TranscodeRunner
    ) async -> String? {
        var scored: [(frames: Int, mean: Double)] = []

        for frames in framesToTry {
            let log = scratch.appendingPathComponent("align-\(frames).json", isDirectory: false)
            defer { try? FileManager.default.removeItem(at: log) }

            let probe = truncate(
                command.materialise(
                    distorted: candidate, reference: source, log: log,
                    distortedShift: shift(frames: frames, frameSeconds: frameSeconds)),
                seconds: probeSeconds)
            guard let run = try? await runner.run(ffmpeg, probe, progress: { _ in }),
                  run.exitCode == 0,
                  let text = try? String(contentsOf: log, encoding: .utf8),
                  let score = meanScore(text)
            else { continue }
            scored.append((frames, score))
        }

        return choose(scored).map { shift(frames: $0, frameSeconds: frameSeconds) }
    }

    /// The measurement stopped after `seconds` of output: its own limit replaced, or one added
    /// before the output when it has none.
    static func truncate(_ arguments: [String], seconds: Double) -> [String] {
        var arguments = arguments
        let length = String(format: "%g", seconds)
        if let limit = arguments.lastIndex(of: "-t"), limit + 1 < arguments.count {
            arguments[limit + 1] = length
        } else {
            arguments.insert(contentsOf: ["-t", length], at: arguments.lastIndex(of: "-f") ?? arguments.count)
        }
        return arguments
    }

    /// The offset, in frames, whose probe matched best, or nil when none could be scored. The
    /// unshifted timeline keeps the window unless another offset beats it clearly.
    static func choose(_ scored: [(frames: Int, mean: Double)]) -> Int? {
        guard let best = scored.max(by: { $0.mean < $1.mean }) else { return nil }
        if best.frames != 0,
           let unshifted = scored.first(where: { $0.frames == 0 }),
           best.mean - unshifted.mean < choiceMarginPoints {
            return 0
        }
        return best.frames
    }

    /// The token for moving the candidate `frames` pictures later, as the server writes seconds.
    static func shift(frames: Int, frameSeconds: Double) -> String {
        TimelineLead.shift(candidate: 0, source: -Double(frames) * frameSeconds)
    }

    /// How long one picture lasts, from the source's own declared rate. Nil when it cannot be
    /// read, which leaves the caller its own default rather than a guess dressed as a measurement.
    public static func frameSeconds(
        ffprobe: URL?, file: URL, runner: CommandRunner
    ) async -> Double? {
        guard let ffprobe else { return nil }
        let result = await runner.run(ffprobe, [
            "-v", "error", "-select_streams", FullVerification.movingPictureStreamSpecifier,
            "-show_entries", "stream=r_frame_rate", "-of", "csv=p=0", file.path,
        ])
        guard result.exitCode == 0 else { return nil }
        let parts = result.output.trimmingCharacters(in: .whitespacesAndNewlines).split(separator: "/")
        guard parts.count == 2, let numerator = Double(parts[0]), let denominator = Double(parts[1]),
              numerator > 0, denominator > 0
        else { return nil }
        return denominator / numerator
    }

    /// The mean of a probe's frame scores. The mean rather than the harmonic mean on purpose: this
    /// is choosing between alignments, not judging quality, and the mean separates them cleanly
    /// while staying readable when every alignment is poor.
    static func meanScore(_ json: String) -> Double? {
        guard let data = json.data(using: .utf8),
              let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let frames = root["frames"] as? [[String: Any]]
        else { return nil }
        let scores = frames.compactMap { ($0["metrics"] as? [String: Any])?["vmaf"] as? Double }
        guard !scores.isEmpty else { return nil }
        return scores.reduce(0, +) / Double(scores.count)
    }
}
