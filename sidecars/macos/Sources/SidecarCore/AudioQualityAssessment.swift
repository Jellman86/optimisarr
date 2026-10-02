import Foundation

public struct AudioQualityProfile: Codable, Sendable {
    let durationSeconds: Double
    let channels: Int
    let sampleRate: Int
    let channelLayout: String
}
public struct AudioQualitySample: Codable, Sendable {
    let startSeconds: Double
    let durationSeconds: Double
}
public struct AudioQualityDistances: Codable, Sendable {
    let frames: Int
    let channelDistances: [Double]
}
public struct AudioQualityWindowResult: Codable, Sendable {
    let window: AudioQualitySample
    let distances: AudioQualityDistances
}
public struct AudioQualityResult: Codable, Sendable {
    var measured = false
    var error: String?
    var reference: AudioQualityProfile?
    var candidate: AudioQualityProfile?
    var referenceSha256: String?
    var candidateSha256: String?
    var metricSha256: String?
    var ffmpegSha256: String?
    var ffprobeSha256: String?
    var windows: [AudioQualityWindowResult] = []
    var elapsedSeconds = 0.0
}
public struct RemoteAudioQualityEvidence: Codable, Sendable {
    let metric = "zimtohrli"
    let revision = AudioQualityAssessment.revision
    let preparation = "audio-f32le-48k-native-defaults-v1"
    let assessment: AudioQualityResult
}

/// Bounded experimental observations; neither the score nor failure changes verification gates.
public struct AudioQualityAssessment: Sendable {
    static let revision = "f9e7364df2f6a41f761f513b7ea6be7e2d6f2ce3"
    let runner: any TranscodeRunner
    public init(runner: any TranscodeRunner = ProcessTranscodeRunner()) { self.runner = runner }

    static func plan(_ seconds: Double) -> [AudioQualitySample] {
        guard seconds.isFinite, seconds >= 1, seconds <= 86400 else { return [] }
        let frames = Int(floor(seconds * 48000)), duration = Double(frames) / 48000
        if duration > 90 {
            return [.init(startSeconds: 0, durationSeconds: 30),
                .init(startSeconds: floor((duration - 30) * 24000) / 48000, durationSeconds: 30),
                .init(startSeconds: duration - 30, durationSeconds: 30)]
        }
        let count = Int(ceil(duration / 30)), length = frames / count, remainder = frames % count
        return (0..<count).map { index in .init(
            startSeconds: Double(index * length + min(index, remainder)) / 48000,
            durationSeconds: Double(length + (index < remainder ? 1 : 0)) / 48000) }
    }

    static func profile(_ json: String) -> AudioQualityProfile? {
        guard let data = json.data(using: .utf8),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let streams = object["streams"] as? [[String: Any]] else { return nil }
        let audio = streams.filter { $0["codec_type"] as? String == "audio" }
        guard audio.count == 1, !streams.contains(where: { stream in
            stream["codec_type"] as? String == "video" && (stream["disposition"] as? [String: Any])?["attached_pic"] as? Int != 1
        }), let channels = audio[0]["channels"] as? Int, [1, 2].contains(channels),
              let rateText = audio[0]["sample_rate"] as? String, let rate = Int(rateText), (8000...384000).contains(rate) else { return nil }
        let layout = channels == 1 ? "mono" : "stereo"
        if let named = audio[0]["channel_layout"] as? String, !named.isEmpty && named != layout { return nil }
        let streamDuration = audio[0]["duration"] as? String
        let formatDuration = (object["format"] as? [String: Any])?["duration"] as? String
        guard let text = streamDuration == nil || streamDuration == "N/A" ? formatDuration : streamDuration,
              let seconds = Double(text), !plan(seconds).isEmpty else { return nil }
        return .init(durationSeconds: seconds, channels: channels, sampleRate: rate, channelLayout: layout)
    }

    static func parse(_ json: String, channels: Int, seconds: Double) -> AudioQualityDistances? {
        struct Native: Decodable {
            let schema: Int, metric: String, revision: String, sampleRate: Int, channels: Int
            let fullScaleSineDb: Double, frames: Int, distances: [Double]
        }
        guard json.utf8.count <= 8192, [1, 2].contains(channels), seconds.isFinite, (1...30).contains(seconds),
              let data = json.data(using: .utf8), let result = try? JSONDecoder().decode(Native.self, from: data),
              result.schema == 1, result.metric == "zimtohrli", result.revision == revision,
              result.sampleRate == 48000, result.channels == channels, abs(result.fullScaleSineDb - 78.3) < 0.0001,
              abs(Double(result.frames) - seconds * 48000) <= 64, result.distances.count == channels,
              result.distances.allSatisfy({ $0.isFinite && (0...1).contains($0) }) else { return nil }
        return .init(frames: result.frames, channelDistances: result.distances)
    }

    public func measure(ffmpeg: URL, ffprobe: URL, metric: URL, source: URL, candidate: URL,
                        sourceProbe: String, candidateProbe: String, scratch: URL) async throws -> RemoteAudioQualityEvidence {
        try Task.checkCancellation()
        let started = Date(), owned = scratch.appendingPathComponent("audio-assessment-" + UUID().uuidString)
        var result = AudioQualityResult()
        defer { try? FileManager.default.removeItem(at: owned) }
        do {
            guard let reference = Self.profile(sourceProbe), let output = Self.profile(candidateProbe) else {
                throw Failure("Assessment requires one known-duration mono/stereo audio track without video or unproved channel layouts.")
            }
            result.reference = reference; result.candidate = output
            guard reference.channels == output.channels, reference.channelLayout == output.channelLayout,
                  abs(reference.durationSeconds - output.durationSeconds) <= 0.1 else {
                throw Failure("The candidate's channels or duration do not match the reference.")
            }
            result.referenceSha256 = try JobRunner.sha256(of: source)
            result.candidateSha256 = try JobRunner.sha256(of: candidate)
            result.metricSha256 = try JobRunner.sha256(of: metric)
            result.ffmpegSha256 = try JobRunner.sha256(of: ffmpeg)
            result.ffprobeSha256 = try JobRunner.sha256(of: ffprobe)
            try FileManager.default.createDirectory(at: owned, withIntermediateDirectories: false,
                attributes: [.posixPermissions: 0o700])
            for window in Self.plan(min(reference.durationSeconds, output.durationSeconds)) {
                let a = owned.appendingPathComponent("reference.raw"), b = owned.appendingPathComponent("candidate.raw")
                for (input, pcm) in [(source, a), (candidate, b)] {
                    let decoded = try await run(ffmpeg, ["-nostdin", "-hide_banner", "-nostats", "-v", "error", "-xerror", "-n",
                        "-ss", String(window.startSeconds), "-i", input.path, "-map", "0:a:0", "-t", String(window.durationSeconds),
                        "-vn", "-sn", "-dn", "-ar", "48000", "-c:a", "pcm_f32le", "-fs", "11520004", "-f", "f32le", pcm.path])
                    guard decoded.exitCode == 0 else { throw Failure("Audio preparation failed: \(decoded.stderr)") }
                    let bytes = try size(pcm), stride = Int64(reference.channels * 4)
                    guard bytes >= 192000, bytes <= 11520000, bytes % stride == 0,
                          abs(Double(bytes / stride) - window.durationSeconds * 48000) <= 64 else { throw Failure("Prepared audio does not cover the assigned frames.") }
                }
                guard try size(a) == size(b) else { throw Failure("Prepared reference and candidate frame counts differ.") }
                let report = owned.appendingPathComponent("metric.json")
                let scored = try await run(metric, [a.path, b.path, String(reference.channels), "--report", report.path])
                guard scored.exitCode == 0, try size(report) <= 8192,
                      let distances = Self.parse(try String(contentsOf: report, encoding: .utf8), channels: reference.channels,
                        seconds: window.durationSeconds) else { throw Failure("Audio metric evidence is incomplete or does not match this release.") }
                result.windows.append(.init(window: window, distances: distances))
                for file in [a, b, report] { try FileManager.default.removeItem(at: file) }
            }
            guard try JobRunner.sha256(of: source) == result.referenceSha256,
                  try JobRunner.sha256(of: candidate) == result.candidateSha256,
                  try JobRunner.sha256(of: metric) == result.metricSha256,
                  try JobRunner.sha256(of: ffmpeg) == result.ffmpegSha256,
                  try JobRunner.sha256(of: ffprobe) == result.ffprobeSha256 else {
                throw Failure("A media file or tool changed during assessment.")
            }
            result.measured = true
        } catch is CancellationError { throw CancellationError() }
          catch { result.error = String(describing: error) }
        try Task.checkCancellation()
        result.elapsedSeconds = Date().timeIntervalSince(started)
        return .init(assessment: result)
    }

    private func size(_ file: URL) throws -> Int64 {
        (try FileManager.default.attributesOfItem(atPath: file.path)[.size] as? NSNumber)?.int64Value ?? 0
    }
    private func run(_ executable: URL, _ arguments: [String]) async throws -> (exitCode: Int32, stderr: String) {
        try await withThrowingTaskGroup(of: CommandResult.self) { group in
            group.addTask { let value = try await runner.run(executable, arguments) { _ in }; return .init(exitCode: value.exitCode, stderr: value.stderr) }
            group.addTask { try await Task.sleep(for: .seconds(90)); throw Failure("Audio assessment command exceeded its 90-second limit.") }
            defer { group.cancelAll() }
            let value = try await group.next()!
            return (value.exitCode, value.stderr)
        }
    }
    private struct CommandResult: Sendable { let exitCode: Int32; let stderr: String }
    private struct Failure: Error, CustomStringConvertible { let description: String; init(_ message: String) { description = message } }
}
