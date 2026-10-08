using Optimisarr.Core.Tools;

namespace Optimisarr.Core.Queue;

/// <summary>Whether a video source's own timing can be trusted, or its presentation times must be regenerated.</summary>
/// <remarks>
/// <c>-fflags +genpts</c> fills in presentation times a source does not store. For a stream with
/// B-frames that carries decode times only (VC-1 or H.264 in Matroska's VfW mode) it assigns them in
/// decode order, so the first reordered pictures reach the encoder late or repeated (#373). The
/// decoder's own timing is regular there. The flag is format-wide, though, and a copied stream with
/// untimed packets still needs it to mux, so it is dropped only when every copied stream proves its
/// own times in the sampled head.
/// </remarks>
public sealed class SourceTimestampFacts(string ffprobeCommand = "ffprobe")
{
    public static IReadOnlyList<string> Arguments(string path) =>
    [
        "-v", "error", "-read_intervals", "%+#256",
        "-show_entries", "stream=index,codec_type:packet=codec_type,stream_index,pts,dts", "-of", "csv=p=1", path
    ];

    /// <param name="output">ffprobe's <c>packet,codec_type,stream_index,pts,dts</c> lines for the source head
    /// and <c>stream,index,codec_type</c> lines for every stream.</param>
    /// <param name="audioIsCopied">False when every retained audio track is re-encoded, and so timed by its decoder.</param>
    /// <param name="otherStreamsKept">False when the output keeps only the encoded video stream.</param>
    public static bool NeedsGeneratedPresentationTimes(string output, bool audioIsCopied, bool otherStreamsKept = true)
    {
        var streams = new Dictionary<int, string>();
        var packets = new List<(int Stream, bool Presented, bool Decoded)>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(',');
            if (fields is ["stream", var index, var type] && int.TryParse(index, out var streamIndex))
                streams[streamIndex] = type;
            else if (fields is ["packet", _, var packetStream, var pts, var dts] && int.TryParse(packetStream, out var packetIndex))
                packets.Add((packetIndex, HasTime(pts), HasTime(dts)));
            else
                return true;
        }

        // The command re-encodes v:0, the first video stream in source order. Every other kept
        // stream — a second video track or cover picture included — is copied, and needs its own
        // times proven by the sample: one that starts after the head read has no evidence at all.
        var video = streams.Where(stream => stream.Value == "video").Select(stream => stream.Key).ToList();
        if (video.Count == 0)
            return true;
        var encoded = video.Min();
        var encodedPackets = packets.Where(packet => packet.Stream == encoded).ToList();
        if (encodedPackets.Count == 0 || !encodedPackets.All(packet => !packet.Presented && packet.Decoded))
            return true;
        if (!otherStreamsKept)
            return false;
        return streams.Where(stream => stream.Key != encoded && stream.Value != "attachment"
                && (stream.Value != "audio" || audioIsCopied))
            .Any(stream => packets.Where(packet => packet.Stream == stream.Key) is var own
                && (!own.Any() || own.Any(packet => !packet.Presented)));
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
