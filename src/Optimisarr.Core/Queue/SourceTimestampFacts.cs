using Optimisarr.Core.Tools;

namespace Optimisarr.Core.Queue;

/// <summary>Whether a video source's own timing can be trusted, or its presentation times must be regenerated.</summary>
/// <remarks>
/// <c>-fflags +genpts</c> fills in presentation times a source does not store. For a stream with
/// B-frames that carries decode times only (VC-1 or H.264 in Matroska's VfW mode) it assigns them in
/// decode order, so the first reordered pictures reach the encoder late or repeated (#373). The
/// decoder's own timing is regular there. The flag is format-wide, though, and a copied stream with
/// untimed packets still needs it to mux, so it is dropped only when nothing copied depends on it.
/// </remarks>
public sealed class SourceTimestampFacts(string ffprobeCommand = "ffprobe")
{
    public static IReadOnlyList<string> Arguments(string path) =>
    [
        "-v", "error", "-read_intervals", "%+#256",
        "-show_entries", "packet=codec_type,stream_index,pts,dts", "-of", "csv=p=0", path
    ];

    /// <param name="packets">ffprobe's <c>codec_type,stream_index,pts,dts</c> lines for the source head.</param>
    /// <param name="audioIsCopied">False when every retained audio track is re-encoded, and so timed by its decoder.</param>
    public static bool NeedsGeneratedPresentationTimes(string packets, bool audioIsCopied)
    {
        var video = new List<(int Stream, bool Presented, bool Decoded)>();
        var copiedStreamLacksTiming = false;
        foreach (var line in packets.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(',');
            if (fields.Length < 4 || !int.TryParse(fields[1], out var stream))
                return true;
            var presented = HasTime(fields[2]);
            if (fields[0] == "video")
                video.Add((stream, presented, HasTime(fields[3])));
            else if (!presented && (fields[0] != "audio" || audioIsCopied))
                copiedStreamLacksTiming = true;
        }

        if (video.Count == 0 || copiedStreamLacksTiming)
            return true;
        // A cover picture is a video stream with one timed packet; the main stream is the busiest.
        var main = video.GroupBy(packet => packet.Stream).MaxBy(group => group.Count())!;
        return !main.All(packet => !packet.Presented && packet.Decoded);
    }

    public async Task<bool> NeedsGeneratedPresentationTimesAsync(string path, bool audioIsCopied, CancellationToken cancellationToken)
    {
        var result = await BoundedToolProcess.RunAsync(ffprobeCommand, Arguments(path), cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("The source's timestamp probe failed: " + result.Error);
        return NeedsGeneratedPresentationTimes(result.Output, audioIsCopied);
    }

    private static bool HasTime(string value) => value.Length > 0 && value != "N/A";
}
