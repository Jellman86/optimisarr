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

    /// Counts a file's pictures by reading its packets, not decoding them. Nil when it cannot say.
    static func count(ffprobe: URL?, file: URL, runner: CommandRunner) async -> Int? {
        guard let ffprobe else { return nil }
        let result = await runner.run(ffprobe, [
            "-v", "error", "-select_streams", FullVerification.movingPictureStreamSpecifier, "-count_packets",
            "-show_entries", "stream=nb_read_packets", "-of", "csv=p=0", file.path,
        ])
        guard result.exitCode == 0 else { return nil }
        var text = result.output.trimmingCharacters(in: .whitespacesAndNewlines)
        if text.hasSuffix(",") { text.removeLast() }
        guard let count = Int(text), count >= 0 else { return nil }
        return count
    }
}
