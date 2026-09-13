import Foundation

/// How far into its container a file's first picture sits.
///
/// FFmpeg seeks and stamps frames relative to the container start, which is the earliest stream —
/// often audio, which can lead the video by a frame or so of priming. Two files whose pictures
/// match frame for frame can therefore present them at different instants, and a sampled VMAF
/// window that pairs frames by timestamp then compares neighbours instead of the same picture.
/// The server builds the measurement with a token where that difference goes; this measures it.
public enum TimelineLead {
    static let ffprobeArguments = [
        "-v", "error",
        "-show_entries", "format=start_time:stream=codec_type,start_time",
        "-of", "json",
    ]

    /// The video stream's start less the container's, in seconds, or nil when either is missing.
    public static func parse(_ json: String) -> Double? {
        guard let data = json.data(using: .utf8),
              let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let format = root["format"] as? [String: Any],
              let container = seconds(format["start_time"]),
              let streams = root["streams"] as? [[String: Any]],
              let video = streams.first(where: { $0["codec_type"] as? String == "video" }),
              let picture = seconds(video["start_time"])
        else { return nil }
        return picture - container
    }

    public static func measure(ffprobe: URL, file: URL, runner: CommandRunner) async -> Double? {
        let result = await runner.run(ffprobe, ffprobeArguments + [file.path])
        guard result.exitCode == 0 else { return nil }
        return parse(result.output)
    }

    /// The seconds by which the candidate presents a picture later than the source, formatted the
    /// way the server formats seconds in a filter graph: up to six decimals, no trailing zeros.
    public static func shift(candidate: Double, source: Double) -> String {
        let value = (candidate - source * 1_000_000).rounded() == 0 ? 0 : candidate - source
        var text = String(format: "%.6f", value)
        while text.hasSuffix("0") { text.removeLast() }
        if text.hasSuffix(".") { text.removeLast() }
        return text == "-0" ? "0" : text
    }

    private static func seconds(_ value: Any?) -> Double? {
        if let text = value as? String { return Double(text) }
        if let number = value as? NSNumber { return number.doubleValue }
        return nil
    }
}
