import Foundation

public struct AudioQualityProfile: Codable, Sendable {
    let durationSeconds: Double
    let channels: Int
    let sampleRate: Int
    let channelLayout: String
    var containerLeadSeconds: Double = 0
    enum CodingKeys: String, CodingKey { case durationSeconds, channels, sampleRate, channelLayout, containerLeadSeconds }
    init(durationSeconds: Double, channels: Int, sampleRate: Int, channelLayout: String, containerLeadSeconds: Double = 0) {
        self.durationSeconds = durationSeconds; self.channels = channels; self.sampleRate = sampleRate
        self.channelLayout = channelLayout; self.containerLeadSeconds = containerLeadSeconds
    }
    public init(from decoder: any Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        durationSeconds = try values.decode(Double.self, forKey: .durationSeconds)
        channels = try values.decode(Int.self, forKey: .channels)
        sampleRate = try values.decode(Int.self, forKey: .sampleRate)
        channelLayout = try values.decode(String.self, forKey: .channelLayout)
        containerLeadSeconds = try values.decodeIfPresent(Double.self, forKey: .containerLeadSeconds) ?? 0
    }
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
    var referenceAudioIndex: Int?
    var candidateAudioIndex: Int?
}
public struct RemoteAudioQualityEvidence: Codable, Sendable {
    let metric = "zimtohrli"
    let revision = AudioQualityAssessment.revision
    var preparation: String { assessment.referenceAudioIndex == nil ? "audio-f32le-48k-native-defaults-v1" : "audio-f32le-48k-video-timeline-v1" }
    enum CodingKeys: String, CodingKey { case metric, revision, preparation, assessment }
    public init(assessment: AudioQualityResult) { self.assessment = assessment }
    public init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        assessment = try container.decode(AudioQualityResult.self, forKey: .assessment)
        guard try container.decode(String.self, forKey: .metric) == metric,
              try container.decode(String.self, forKey: .revision) == revision,
              try container.decode(String.self, forKey: .preparation) == preparation else {
            throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "Unsupported audio assessment preparation."))
        }
    }
    public func encode(to encoder: any Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(metric, forKey: .metric); try container.encode(revision, forKey: .revision)
        try container.encode(preparation, forKey: .preparation); try container.encode(assessment, forKey: .assessment)
    }
    let assessment: AudioQualityResult
}

public struct AudioQualityFileHashes: Equatable, Sendable {
    let reference: String, candidate: String, metric: String, ffmpeg: String, ffprobe: String
    static func read(source: URL, candidate: URL, metric: URL, ffmpeg: URL, ffprobe: URL,
                     hash: @Sendable (URL) throws -> String) throws -> Self {
        .init(reference: try hash(source), candidate: try hash(candidate), metric: try hash(metric),
              ffmpeg: try hash(ffmpeg), ffprobe: try hash(ffprobe))
    }
}

/// Bounded experimental observations; neither the score nor failure changes verification gates.
public struct AudioQualityAssessment: Sendable {
    static let revision = "f9e7364df2f6a41f761f513b7ea6be7e2d6f2ce3"
    let runner: any TranscodeRunner
    let hash: @Sendable (URL) throws -> String
    public init(runner: any TranscodeRunner = ProcessTranscodeRunner()) { self.runner = runner; self.hash = JobRunner.sha256 }
    init(runner: any TranscodeRunner, hash: @escaping @Sendable (URL) throws -> String) { self.runner = runner; self.hash = hash }
    static func secondsText(_ seconds: Double) -> String {
        String(format: "%.8f", locale: Locale(identifier: "en_US_POSIX"), seconds).replacingOccurrences(of: #"\.?0+$"#, with: "", options: .regularExpression)
    }

    static func plan(_ durationSeconds: Double, soundtrack: Bool = false) -> [AudioQualitySample] {
        guard durationSeconds.isFinite, durationSeconds >= 1, durationSeconds <= 86400 else { return [] }
        let seconds = durationSeconds - (soundtrack ? min(0.1, durationSeconds - 1) : 0)
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

    static func profile(_ json: String, audioIndex: Int? = nil) -> AudioQualityProfile? {
        guard let data = json.data(using: .utf8),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let streams = object["streams"] as? [[String: Any]] else { return nil }
        let audio = streams.filter { $0["codec_type"] as? String == "audio" }
        let selected = audioIndex ?? 0
        guard selected >= 0, selected < audio.count, (audioIndex != nil || (audio.count == 1 && !streams.contains(where: { stream in
            stream["codec_type"] as? String == "video" && (stream["disposition"] as? [String: Any])?["attached_pic"] as? Int != 1
        }))), let channels = audio[selected]["channels"] as? Int, [1, 2].contains(channels),
              let rateText = audio[selected]["sample_rate"] as? String, let rate = Int(rateText), (8000...384000).contains(rate) else { return nil }
        let layout = channels == 1 ? "mono" : "stereo"
        if let named = audio[selected]["channel_layout"] as? String, !named.isEmpty && named != layout { return nil }
        let streamDuration = audio[selected]["duration"] as? String
        let formatDuration = (object["format"] as? [String: Any])?["duration"] as? String
        var duration = streamDuration.flatMap(Double.init)
        if duration == nil, audioIndex != nil,
           let tags = audio[selected]["tags"] as? [String: Any],
           let endText = tags.first(where: { $0.key.uppercased() == "DURATION" })?.value as? String {
            let parts = endText.split(separator: ":", omittingEmptySubsequences: false)
            guard parts.count == 3, let hours = Int(parts[0]), hours >= 0,
                  let minutes = Int(parts[1]), (0...59).contains(minutes),
                  let seconds = Double(parts[2]), (0..<60).contains(seconds),
                  let start = Double(audio[selected]["start_time"] as? String ?? "N/A") else { return nil }
            let statistics = (tags.first(where: { $0.key.uppercased() == "_STATISTICS_WRITING_APP" })?.value as? String ?? "").lowercased()
            let formatTags = (object["format"] as? [String: Any])?["tags"] as? [String: Any]
            let encoder = (formatTags?.first(where: { $0.key.lowercased() == "encoder" })?.value as? String ?? "").lowercased()
            let lengthTag = statistics.hasPrefix("mkvmerge") || statistics.hasPrefix("mkvpropedit")
            // A later tag edit and stale copied statistics have indistinguishable provenance.
            guard !(lengthTag && encoder.hasPrefix("lavf") && start != 0) else { return nil }
            duration = Double(hours) * 3600 + Double(minutes) * 60 + seconds - (lengthTag ? 0 : start)
        }
        if duration == nil, audioIndex == nil { duration = formatDuration.flatMap(Double.init) }
        guard let seconds = duration, !plan(seconds).isEmpty else { return nil }
        var lead = 0.0
        if audioIndex != nil, let video = streams.first(where: { $0["codec_type"] as? String == "video"
            && ($0["disposition"] as? [String: Any])?["attached_pic"] as? Int != 1 }) {
            let videoStart = Double(video["start_time"] as? String ?? "N/A") ?? .nan
            let containerStart = Double((object["format"] as? [String: Any])?["start_time"] as? String ?? "N/A") ?? .nan
            lead = videoStart - containerStart
            guard lead.isFinite, (0...0.1).contains(lead) else { return nil }
        }
        return .init(durationSeconds: seconds, channels: channels, sampleRate: rate, channelLayout: layout, containerLeadSeconds: lead)
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
                        sourceProbe: String, candidateProbe: String, scratch: URL, sourceAudioIndex: Int? = nil, candidateAudioIndex: Int? = nil, sharedHashes: AudioQualityFileHashes? = nil) async throws -> RemoteAudioQualityEvidence {
        try Task.checkCancellation()
        let started = Date(), owned = scratch.appendingPathComponent("audio-assessment-" + UUID().uuidString)
        var result = AudioQualityResult()
        result.referenceAudioIndex = sourceAudioIndex
        result.candidateAudioIndex = candidateAudioIndex
        defer { try? FileManager.default.removeItem(at: owned) }
        do {
            guard let reference = Self.profile(sourceProbe, audioIndex: sourceAudioIndex), let output = Self.profile(candidateProbe, audioIndex: candidateAudioIndex) else {
                throw Failure("Assessment requires one known-duration mono/stereo audio track without video or unproved channel layouts.")
            }
            result.reference = reference; result.candidate = output
            guard reference.channels == output.channels, reference.channelLayout == output.channelLayout,
                  abs(reference.durationSeconds - output.durationSeconds) <= 0.1 else {
                throw Failure("The candidate's channels or duration do not match the reference.")
            }
            let hashes = try sharedHashes ?? AudioQualityFileHashes.read(source: source, candidate: candidate,
                metric: metric, ffmpeg: ffmpeg, ffprobe: ffprobe, hash: hash)
            result.referenceSha256 = hashes.reference; result.candidateSha256 = hashes.candidate
            result.metricSha256 = hashes.metric; result.ffmpegSha256 = hashes.ffmpeg; result.ffprobeSha256 = hashes.ffprobe
            try FileManager.default.createDirectory(at: owned, withIntermediateDirectories: false,
                attributes: [.posixPermissions: 0o700])
            for window in Self.plan(min(reference.durationSeconds, output.durationSeconds), soundtrack: sourceAudioIndex != nil) {
                let a = owned.appendingPathComponent("reference.raw"), b = owned.appendingPathComponent("candidate.raw")
                for (input, pcm, audioIndex, lead) in [(source, a, sourceAudioIndex ?? 0, reference.containerLeadSeconds), (candidate, b, candidateAudioIndex ?? 0, output.containerLeadSeconds)] {
                    let seek = window.startSeconds + lead
                    let soundtrack = sourceAudioIndex != nil
                    let seekArgs = soundtrack && seek == 0 ? [] : ["-ss", Self.secondsText(seek)]
                    let filterArgs = soundtrack ? ["-af", "aresample=48000,atrim=end_sample=\(Int((window.durationSeconds * 48000).rounded()))"] : []
                    let decoded = try await run(ffmpeg, ["-nostdin", "-hide_banner", "-nostats", "-v", "error", "-xerror", "-n",
                        ] + seekArgs + ["-i", input.path, "-map", "0:a:\(audioIndex)", "-t", Self.secondsText(window.durationSeconds + (soundtrack ? 0.1 : 0)),
                        "-vn", "-sn", "-dn", "-ar", "48000"] + filterArgs + ["-c:a", "pcm_f32le", "-fs", "11520004", "-f", "f32le", pcm.path])
                    guard decoded.exitCode == 0 else { throw Failure("Audio preparation failed: \(decoded.stderr)") }
                    let bytes = try size(pcm), stride = Int64(reference.channels * 4)
                    guard bytes >= 192000, bytes <= 11520000, bytes % stride == 0,
                          abs(Double(bytes / stride) - window.durationSeconds * 48000) <= 64 else { throw Failure("Prepared audio does not cover the assigned frames.") }
                }
                if try size(a) != size(b) {
                    guard sourceAudioIndex != nil else { throw Failure("Prepared reference and candidate frame counts differ.") }
                    // Each input has passed the rounding budget. Trim excess samples; never pad.
                    let common = try min(size(a), size(b))
                    for pcm in [a, b] {
                        let handle = try FileHandle(forWritingTo: pcm)
                        defer { try? handle.close() }
                        try handle.truncate(atOffset: UInt64(common))
                    }
                }
                let report = owned.appendingPathComponent("metric.json")
                let scored = try await run(metric, [a.path, b.path, String(reference.channels), "--report", report.path])
                guard scored.exitCode == 0, try size(report) <= 8192,
                      let distances = Self.parse(try String(contentsOf: report, encoding: .utf8), channels: reference.channels,
                        seconds: window.durationSeconds) else { throw Failure("Audio metric evidence is incomplete or does not match this release.") }
                result.windows.append(.init(window: window, distances: distances))
                for file in [a, b, report] { try FileManager.default.removeItem(at: file) }
            }
            if try sharedHashes == nil && AudioQualityFileHashes.read(source: source, candidate: candidate,
                metric: metric, ffmpeg: ffmpeg, ffprobe: ffprobe, hash: hash) != hashes {
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
