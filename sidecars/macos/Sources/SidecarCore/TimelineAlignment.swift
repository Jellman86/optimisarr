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
/// So this measures it instead. The worker is the only machine holding both files, a couple of
/// seconds of pictures is enough to tell a frame's misalignment from a good match, and the answer
/// is the one the real measurement then uses.
public enum TimelineAlignment {
    /// Offsets to try, in frames. One frame either way covers every case seen; a candidate further
    /// out than that is not misaligned, it is a different file.
    static let framesToTry = [0, 1, -1]

    /// Where to take the probe from, and how much of it. Far enough in to be past titles and
    /// black, short enough that three of them cost a second or two.
    static let probeStartSeconds = 60.0
    static let probeLeadSeconds = 1.0
    static let probeSeconds = 2.0

    /// The shift to hand the server's measurement commands, written the way it writes seconds.
    /// Nil when no probe could be scored at all, which the caller treats as it treats any other
    /// measurement it could not make.
    public static func measure(
        ffmpeg: URL,
        source: URL,
        candidate: URL,
        frameSeconds: Double = 1.0 / 25.0,
        scratch: URL,
        runner: TranscodeRunner
    ) async -> String? {
        var best: (shift: Double, score: Double)?

        for frames in framesToTry {
            let shift = Double(frames) * frameSeconds
            let log = scratch.appendingPathComponent("align-\(frames).json", isDirectory: false)
            defer { try? FileManager.default.removeItem(at: log) }

            guard let run = try? await runner.run(
                ffmpeg,
                arguments(source: source, candidate: candidate, shift: shift, log: log),
                progress: { _ in }),
                run.exitCode == 0,
                let text = try? String(contentsOf: log, encoding: .utf8),
                let score = meanScore(text)
            else { continue }

            if best == nil || score > best!.score {
                best = (shift, score)
            }
        }

        guard let best else { return nil }
        return TimelineLead.shift(candidate: 0, source: -best.shift)
    }

    /// The probe: the same pairing the real measurement does, on a short window, at a small size.
    /// Same shape deliberately — an alignment chosen by a differently-built comparison would be
    /// the alignment for a measurement nobody runs.
    static func arguments(source: URL, candidate: URL, shift: Double, log: URL) -> [String] {
        let lead = String(format: "%g", probeLeadSeconds)
        let length = String(format: "%g", probeSeconds)
        let offset = String(format: "%.6f", shift * 1_000_000)
        let graph = """
            [0:v]settb=AVTB,setpts=PTS-\(offset),trim=start=\(lead):duration=\(length),\
            settb=AVTB,setpts=PTS-STARTPTS,scale=320:240:flags=bilinear,format=yuv420p[dist];\
            [1:v]settb=AVTB,trim=start=\(lead):duration=\(length),\
            settb=AVTB,setpts=PTS-STARTPTS,scale=320:240:flags=bilinear,format=yuv420p[ref];\
            [dist][ref]libvmaf=model=version=vmaf_v0.6.1:n_threads=4:n_subsample=1:\
            log_fmt=json:log_path=\(escapedForFilterOption(log.path)):shortest=1:repeatlast=0
            """
        return [
            "-nostdin", "-v", "error",
            "-ss", String(format: "%g", probeStartSeconds), "-i", candidate.path,
            "-ss", String(format: "%g", probeStartSeconds), "-i", source.path,
            "-lavfi", graph,
            "-t", length, "-f", "null", "-",
        ]
    }

    /// How long one picture lasts, from the source's own declared rate. Nil when it cannot be
    /// read, which leaves the caller its own default rather than a guess dressed as a measurement.
    public static func frameSeconds(
        ffprobe: URL?, file: URL, runner: CommandRunner
    ) async -> Double? {
        guard let ffprobe else { return nil }
        let result = await runner.run(ffprobe, [
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=r_frame_rate", "-of", "csv=p=0", file.path,
        ])
        guard result.exitCode == 0 else { return nil }
        let parts = result.output.trimmingCharacters(in: .whitespacesAndNewlines).split(separator: "/")
        guard parts.count == 2, let numerator = Double(parts[0]), let denominator = Double(parts[1]),
              numerator > 0, denominator > 0
        else { return nil }
        return denominator / numerator
    }

    /// A colon inside a filter option ends the option, so one in a path has to survive both the
    /// filtergraph parser and the option parser — which unescape it once each.
    static func escapedForFilterOption(_ path: String) -> String {
        path.replacingOccurrences(of: ":", with: "\\\\:")
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
