import Foundation

/// Reduces a libvmaf JSON log to the few numbers worth reading in a line of log output.
///
/// The mean alone hides the failure that matters. A window whose frames are misaligned in time
/// scores perfectly well on average while individual frames score zero, which is what a harmonic
/// mean and a frame count below a floor expose and an average does not.
public enum VmafLogSummary {
    public static func of(_ json: String) -> String? {
        guard let data = json.data(using: .utf8),
              let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let frames = root["frames"] as? [[String: Any]],
              !frames.isEmpty
        else { return nil }

        let scores = frames.compactMap { ($0["metrics"] as? [String: Any])?["vmaf"] as? Double }
        guard !scores.isEmpty else { return nil }

        let mean = scores.reduce(0, +) / Double(scores.count)
        // Guarded against zero: a single zero-scoring frame would otherwise make the harmonic mean
        // a division by zero rather than the very low number it should be.
        let harmonic = Double(scores.count) / scores.reduce(0) { $0 + 1 / max($1, 0.01) }
        let nearZero = scores.filter { $0 < 5 }.count

        return String(
            format: "%d frames, mean %.2f, harmonic %.2f, min %.2f, %d below 5",
            scores.count, mean, harmonic, scores.min() ?? 0, nearZero)
    }
}
