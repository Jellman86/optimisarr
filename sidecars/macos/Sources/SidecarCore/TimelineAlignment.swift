import Foundation

/// Chooses one offset using evidence across the bounded measurement window. Static openings
/// cannot hide later motion, and conflicting scenes cannot be spliced into a passing score (#406).
public enum TimelineAlignment {
    /// Offsets to try, in frames. One frame either way covers every case seen; a candidate further
    /// out than that is not misaligned, it is a different file.
    static let framesToTry = [0, 1, -1]

    /// Evidence block length, also bounding probes with no explicit window.
    static let probeSeconds = 5.0

    /// How far another offset must beat the unshifted timeline to be chosen. A frame of real
    /// misalignment costs tens of points; anything closer is the scene, not the timeline.
    static let choiceMarginPoints = 2.0

    static let maximumProbeSeconds = 40.0
    static let probeFrameSubsample = 5

    struct AlignmentScore: Sendable {
        let frameNumber: Int
        let score: Double
    }

    static func commandFrameSeconds(_ command: [String]) -> Double? {
        guard let filter = command.firstIndex(of: "-lavfi"), filter + 1 < command.count else { return nil }
        let graph = command[filter + 1]
        let regex = try! NSRegularExpression(pattern: #"(?:fps=fps=|round\(\{\{distortedShift\}\}\*)([0-9.eE+-]+)"#)
        guard let match = regex.firstMatch(in: graph, range: NSRange(graph.startIndex..., in: graph)),
              let range = Range(match.range(at: 1), in: graph), let rate = Double(graph[range]),
              rate > 0, rate <= 1_000, (1 / rate).isFinite else { return nil }
        return 1 / rate
    }

    static func duration(_ command: [String]) -> Double {
        guard let limit = command.lastIndex(of: "-t"), limit + 1 < command.count,
              let seconds = Double(command[limit + 1]), seconds.isFinite, seconds > 0
        else { return probeSeconds }
        return min(seconds, maximumProbeSeconds)
    }

    // Sparse scoring covers the bounded window without changing the final metric policy.
    static func prepare(_ command: [String]) -> [String] {
        var arguments = truncate(command, seconds: duration(command))
        guard let filter = arguments.firstIndex(of: "-lavfi"), filter + 1 < arguments.count else { return arguments }
        var graph = arguments[filter + 1]
        let regex = try! NSRegularExpression(pattern: "n_subsample=([0-9]+)")
        for match in regex.matches(in: graph, range: NSRange(graph.startIndex..., in: graph)).reversed() {
            guard let whole = Range(match.range, in: graph), let value = Range(match.range(at: 1), in: graph),
                  let subsample = Int(graph[value]) else { continue }
            graph.replaceSubrange(whole, with: "n_subsample=\(max(probeFrameSubsample, subsample))")
        }
        arguments[filter + 1] = graph
        return arguments
    }

    static func readScores(_ json: String) -> [AlignmentScore]? {
        guard let data = json.data(using: .utf8),
              let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let frames = root["frames"] as? [[String: Any]], !frames.isEmpty else { return nil }
        var scores: [AlignmentScore] = []
        for frame in frames {
            guard let number = frame["frameNum"] as? Int, number >= 0,
                  scores.last.map({ number > $0.frameNumber }) ?? true,
                  let metrics = frame["metrics"] as? [String: Any],
                  let score = metrics["vmaf"] as? Double, score.isFinite else { return nil }
            scores.append(AlignmentScore(frameNumber: number, score: score))
        }
        return scores
    }

    // Require one correction across the whole window; scene-level winners must never be spliced.
    static func chooseWindow(_ scored: [(frames: Int, scores: [AlignmentScore])],
                             frameRate: Double, duration: Double) -> Int? {
        guard let baseline = scored.first(where: { $0.frames == 0 })?.scores, !baseline.isEmpty else { return nil }
        guard frameRate.isFinite, frameRate > 0, duration.isFinite, duration > 0, duration <= maximumProbeSeconds,
              scored.count == framesToTry.count, Set(scored.map(\.frames)) == Set(framesToTry) else { return 0 }
        let indices = baseline.map(\.frameNumber)
        guard indices.first == 0,
              zip(indices, indices.dropFirst()).allSatisfy({ $1 > $0 }),
              scored.allSatisfy({ $0.scores.map(\.frameNumber) == indices && $0.scores.allSatisfy { $0.score.isFinite } })
        else { return 0 }
        let stride = zip(indices, indices.dropFirst()).map { $1 - $0 }.max() ?? 1
        guard stride <= 10, Double(indices.last!) >= duration * frameRate - Double(2 * stride) - 2,
              Double(indices.last!) <= duration * frameRate + Double(2 * stride) + 2 else { return 0 }
        var blocks: [Int: [Int: Double]] = [:]
        for entry in scored {
            let grouped = Dictionary(grouping: entry.scores) { Int(Double($0.frameNumber) / (probeSeconds * frameRate)) }
            blocks[entry.frames] = grouped.mapValues { values in values.reduce(0) { $0 + $1.score } / Double(values.count) }
        }
        let supported = framesToTry.filter { offset in
            guard offset != 0, let means = blocks[offset] else { return false }
            let others = blocks.filter { $0.key != offset }.map(\.value)
            return means.contains { block, mean in others.allSatisfy { mean - $0[block]! >= choiceMarginPoints } }
                && means.allSatisfy { block, mean in others.allSatisfy { $0[block]! - mean < choiceMarginPoints } }
        }
        return supported.count == 1 ? supported[0] : 0
    }

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
        let frameSeconds = commandFrameSeconds(command.arguments) ?? frameSeconds
        var scored: [(frames: Int, scores: [AlignmentScore])] = []

        for frames in framesToTry {
            let log = scratch.appendingPathComponent("align-\(frames).json", isDirectory: false)
            defer { try? FileManager.default.removeItem(at: log) }

            let probe = prepare(
                command.materialise(
                    distorted: candidate, reference: source, log: log,
                    distortedShift: shift(frames: frames, frameSeconds: frameSeconds)))
            guard let run = try? await runner.run(ffmpeg, probe, progress: { _ in }),
                  run.exitCode == 0,
                  let text = try? String(contentsOf: log, encoding: .utf8),
                  let scores = readScores(text)
            else { continue }
            scored.append((frames, scores))
        }

        return chooseWindow(scored, frameRate: 1 / frameSeconds,
            duration: duration(command.arguments)).map { shift(frames: $0, frameSeconds: frameSeconds) }
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

}
