import Foundation

/// Pure metadata selection/enrichment; the verification runner owns the targeted media reads.
enum FlacStreamInfoProbe {
    static let maximumTrackProbeBytes = 16 * 1024

    static func arguments(file: URL, audioIndex: Int) -> [String] {
        precondition(audioIndex >= 0)
        return ["-v", "error", "-protocol_whitelist", "file", "-format_whitelist", "matroska,flac",
                "-select_streams", "a:\(audioIndex)", "-show_entries", "stream=index,codec_type,codec_name,sample_rate,extradata",
                "-show_data", "-of", "json", file.path]
    }

    static func requiredAudioIndexes(_ metadata: String, writtenByFfmpeg: Bool = false) -> [Int] {
        guard !writtenByFfmpeg, metadata.utf8.count <= 1024 * 1024,
              let data = metadata.data(using: .utf8),
              let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let streams = root["streams"] as? [[String: Any]] else { return [] }
        return streams.filter { $0["codec_type"] as? String == "audio" }.enumerated().compactMap { index, track in
            guard track["codec_name"] as? String == "flac", Double(track["duration"] as? String ?? "") == nil,
                  let tags = track["tags"] as? [String: Any],
                  AudioQualityAssessment.matroskaTag(tags, "DURATION", allowLanguageSuffix: true) != nil else { return nil }
            let writer = (AudioQualityAssessment.matroskaTag(tags, "_STATISTICS_WRITING_APP", allowLanguageSuffix: true) ?? "").lowercased()
            return writer.hasPrefix("mkvmerge") || writer.hasPrefix("mkvpropedit") ? index : nil
        }
    }

    static func withStreamInfo(_ metadata: String, audioIndex: Int, trackProbe: String) -> String {
        guard metadata.utf8.count <= 1024 * 1024, trackProbe.utf8.count <= maximumTrackProbeBytes,
              let data = metadata.data(using: .utf8), let extra = trackProbe.data(using: .utf8),
              var root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              var streams = root["streams"] as? [[String: Any]],
              let targeted = try? JSONSerialization.jsonObject(with: extra) as? [String: Any],
              let selected = targeted["streams"] as? [[String: Any]], selected.count == 1 else { return metadata }
        let audio = streams.indices.filter { streams[$0]["codec_type"] as? String == "audio" }
        guard audio.indices.contains(audioIndex) else { return metadata }
        let index = audio[audioIndex], track = streams[index], info = selected[0]
        guard track["codec_name"] as? String == "flac", info["codec_name"] as? String == "flac",
              info["codec_type"] as? String == "audio", track["sample_rate"] as? String == info["sample_rate"] as? String,
              track["index"] == nil || (track["index"] as? Int == info["index"] as? Int),
              let dump = info["extradata"] as? String, dump.utf8.count <= 1024 else { return metadata }
        streams[index]["extradata"] = dump
        root["streams"] = streams
        guard let enriched = try? JSONSerialization.data(withJSONObject: root), enriched.count <= 1024 * 1024,
              let json = String(data: enriched, encoding: .utf8) else { return metadata }
        return json
    }
}
