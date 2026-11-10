import Foundation

/// Whether a candidate's frames can be compared with its source's by number rather than by
/// timestamp. The same rule as the server's `FramePairing`, so every machine pairs the same way.
///
/// An encode that keeps every frame can still stamp stretches of them a frame early: one candidate
/// held all 34,046 of its source's frames, yet for tens of seconds at a time each carried the
/// previous frame's time. Pairing on timestamps then compared each picture with its neighbour and
/// scored a clean encode at 16; by number the same windows scored 92–94 (#269). When the counts
/// differ a frame was lost or added and every later number is off by it, so the timestamps stay.
enum FramePairing {
    static func applies(source: Int?, candidate: Int?) -> Bool {
        guard let source, let candidate else { return false }
        return source > 0 && source == candidate
    }

    /// Whether every frame window in a measurement command holds pictures and ends within a file of this many.
    /// A window past the last frame decodes nothing, writes no score and still exits cleanly. A
    /// command with no frame window, such as a complete-file comparison, always fits.
    static func fitsWithin(_ command: [String], frames: Int) -> Bool {
        let pattern = try! NSRegularExpression(pattern: #"trim=start_frame=(\d+):end_frame=(\d+)"#)
        for argument in command {
            let range = NSRange(argument.startIndex..., in: argument)
            for match in pattern.matches(in: argument, range: range) {
                guard let startRange = Range(match.range(at: 1), in: argument),
                      let endRange = Range(match.range(at: 2), in: argument),
                      let start = Int(argument[startRange]), let end = Int(argument[endRange]),
                      start < end, end <= frames
                else { return false }
            }
        }
        return true
    }

    /// Counts a file's pictures by decoding frames; packet counts can include non-picture packets. Nil when it cannot say.
    static func count(ffprobe: URL?, file: URL, runner: CommandRunner) async -> Int? {
        guard let ffprobe else { return nil }
        let result = await runner.run(ffprobe, [
            "-v", "error", "-select_streams", FullVerification.movingPictureStreamSpecifier, "-count_frames",
            "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", file.path,
        ])
        guard result.exitCode == 0 else { return nil }
        var text = result.output.trimmingCharacters(in: .whitespacesAndNewlines)
        if text.hasSuffix(",") { text.removeLast() }
        guard let count = Int(text), count >= 0 else { return nil }
        return count
    }
}
