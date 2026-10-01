import Foundation

/// CAMBI sees the actual encoded format, before the measurement scales or converts its pictures.
enum CandidateVideoFormat {
    static let width = "{{encodedWidth}}"
    static let height = "{{encodedHeight}}"
    static let bitDepth = "{{encodedBitDepth}}"

    static func required(_ arguments: [String]) -> Bool {
        arguments.contains { $0.contains(width) || $0.contains(height) || $0.contains(bitDepth) }
    }

    static func probeArguments(_ candidate: URL) -> [String] {
        ["-v", "error", "-show_streams", "-of", "json", candidate.path]
    }

    static func resolve(_ arguments: [String], probeJSON: String?) -> [String]? {
        guard required(arguments) else { return arguments }
        guard let probeJSON, probeJSON.utf8.count <= 1024 * 1024,
              let data = probeJSON.data(using: .utf8),
              let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let streams = root["streams"] as? [[String: Any]] else { return nil }
        for stream in streams {
            guard stream["codec_type"] as? String == "video",
                  (stream["disposition"] as? [String: Any])?["attached_pic"] as? Int != 1 else { continue }
            guard let w = stream["width"] as? Int, w > 0, w <= 32768,
                  let h = stream["height"] as? Int, h > 0, h <= 32768,
                  let pixel = stream["pix_fmt"] as? String else { return nil }
            let depth: Int
            switch pixel {
            case "yuv420p", "yuvj420p", "yuv422p", "yuvj422p", "yuv444p", "yuvj444p", "nv12": depth = 8
            case "yuv420p10le", "yuv422p10le", "yuv444p10le", "p010le": depth = 10
            default: return nil
            }
            if let raw = stream["bits_per_raw_sample"] {
                guard let explicit = Int(String(describing: raw)), explicit >= 0,
                      explicit == 0 || explicit == depth else { return nil }
            }
            return arguments.map { $0.replacingOccurrences(of: width, with: String(w))
                .replacingOccurrences(of: height, with: String(h))
                .replacingOccurrences(of: bitDepth, with: String(depth)) }
        }
        return nil
    }
}
