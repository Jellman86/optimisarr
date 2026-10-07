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
    /// <param name="otherStreamsKept">False when the output keeps only the encoded video stream.</param>
    public static bool NeedsGeneratedPresentationTimes(string packets, bool audioIsCopied, bool otherStreamsKept = true)
    {
        var parsed = new List<(string Type, int Stream, bool Presented, bool Decoded)>();
        foreach (var line in packets.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(',');
            if (fields.Length < 4 || !int.TryParse(fields[1], out var stream))
                return true;
            parsed.Add((fields[0], stream, HasTime(fields[2]), HasTime(fields[3])));
        }

        // The command re-encodes v:0, the first video stream in source order; every other kept
        // stream, a second video track or cover picture included, is copied and must be timed.
        var video = parsed.Where(packet => packet.Type == "video").ToList();
        if (video.Count == 0)
            return true;
        var encoded = video.Min(packet => packet.Stream);
        if (!video.Where(packet => packet.Stream == encoded).All(packet => !packet.Presented && packet.Decoded))
            return true;
        return otherStreamsKept && parsed.Any(packet => packet.Stream != encoded && !packet.Presented
            && (packet.Type != "audio" || audioIsCopied));
    }

    public async Task<bool> NeedsGeneratedPresentationTimesAsync(string path, bool audioIsCopied, bool otherStreamsKept, CancellationToken cancellationToken)
    {
        var result = await BoundedToolProcess.RunAsync(ffprobeCommand, Arguments(path), cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("The source's timestamp probe failed: " + result.Error);
        return NeedsGeneratedPresentationTimes(result.Output, audioIsCopied, otherStreamsKept);
    }

    private static bool HasTime(string value) => value.Length > 0 && value != "N/A";
}
