using System.Text.RegularExpressions;
using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;

namespace Optimisarr.Core.Verification;

/// <summary>
/// What verification compares to prove a trim-only file kept the source's audio between its cuts.
/// A lossless trim must decode to exactly the source's samples over the kept window. A stream copy
/// cannot be compared sample for sample at its edges, because a decoder starting mid-file needs a
/// frame or two to settle (measured: MP3 about 50 ms, Opus about 300 ms, all inside or under the kept
/// silence), so its encoded packets must instead be the source's own, unbroken and unchanged.
/// </summary>
public sealed record KeptAudioEvidence(
    AudioTrimOnlyMethod Method,
    string? SourceDecodedDigest = null,
    string? OutputDecodedDigest = null,
    IReadOnlyList<string>? SourcePacketDigests = null,
    IReadOnlyList<string>? OutputPacketDigests = null);

/// <summary>
/// The tags of a trim-only file and its source, read byte for byte rather than through FFmpeg, which
/// shows a lost or rewritten frame the same as a kept one. Null when they were not read, which fails.
/// </summary>
public sealed record TagsKeptEvidence(MusicTagRead? Source, MusicTagRead? Output);

/// <summary>
/// The tag atoms of a music file written as MP4 and, when it is MP4 too, of its source. A null
/// <paramref name="Source"/> means the source is another format; a null <paramref name="Output"/>
/// means the tags were not read, which fails. <paramref name="SourceLanguage"/> is the language the
/// source's own tags state, which the new file must hold as its iTunes language item.
/// </summary>
public sealed record Mp4TagEvidence(Mp4TagRead? Source, Mp4TagRead? Output, string? SourceLanguage = null);

/// <summary>Reads the per-packet digests of FFmpeg's <c>framehash</c> muxer.</summary>
public static class FrameHashParser
{
    /// <summary>Null when any packet line cannot be read, or there are none.</summary>
    public static IReadOnlyList<string>? Parse(string stdout)
    {
        var digests = new List<string>();
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            // stream, dts, pts, duration, size, hash[, side data…]
            var fields = line.Split(',');
            if (fields.Length < 6 || fields[5].Trim().Length == 0) return null;
            digests.Add(fields[5].Trim());
        }
        return digests.Count > 0 ? digests : null;
    }
}

/// <summary>Reads the single digest of FFmpeg's <c>hash</c> muxer.</summary>
public static partial class HashMuxerParser
{
    [GeneratedRegex(@"^SHA256=([0-9a-fA-F]+)\s*$", RegexOptions.Multiline)]
    private static partial Regex Digest();

    public static string? Parse(string stdout) =>
        Digest().Match(stdout) is { Success: true } match ? match.Groups[1].Value.ToLowerInvariant() : null;
}

public static class PacketRun
{
    /// <summary>
    /// Whether <paramref name="output"/> is a run of consecutive packets of <paramref name="source"/>,
    /// each one identical. Silent packets repeat, so every place the first one matches is tried.
    /// </summary>
    public static bool IsContiguousRunOf(IReadOnlyList<string> output, IReadOnlyList<string> source)
    {
        if (output.Count == 0 || output.Count > source.Count) return false;
        for (var start = 0; start + output.Count <= source.Count; start++)
        {
            var index = 0;
            while (index < output.Count && string.Equals(output[index], source[start + index], StringComparison.Ordinal))
                index++;
            if (index == output.Count) return true;
        }
        return false;
    }
}

/// <summary>The FFmpeg arguments that produce the evidence, writing nothing but the digests.</summary>
public static class KeptAudioCommandBuilder
{
    /// <summary>
    /// The first audio stream decoded, cut to <paramref name="window"/> when one is given, and
    /// hashed as 64-bit floats: every integer depth up to 32 bits and 32-bit float convert to a
    /// double exactly, so equal digests mean equal samples whatever the source's format.
    /// </summary>
    public static IReadOnlyList<string> DecodedDigest(string inputPath, AudioTrimPlan? window)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-i", inputPath, "-map", "0:a:0" };
        if (window is not null)
        {
            args.Add("-af");
            args.Add(window.FilterExpression);
        }
        args.AddRange(["-c:a", "pcm_f64le", "-f", "hash", "-hash", "sha256", "-"]);
        return args;
    }

    public static IReadOnlyList<string> PacketDigests(string inputPath) =>
        ["-hide_banner", "-nostdin", "-v", "error", "-i", inputPath, "-map", "0:a:0",
            "-c", "copy", "-f", "framehash", "-hash", "sha256", "-"];
}

/// <summary>Gathers <see cref="KeptAudioEvidence"/> with FFmpeg. Every failure leaves a digest null, which fails the check.</summary>
public class KeptAudioComparisonService(string ffmpeg)
{
    public virtual async Task<KeptAudioEvidence> CompareAsync(
        string sourcePath, string outputPath, AudioTrimOnlyMethod method, AudioTrimPlan trim, CancellationToken cancellationToken)
    {
        if (method == AudioTrimOnlyMethod.LosslessReencode)
        {
            var source = await FfmpegStderr.RunForStdoutAsync(ffmpeg, KeptAudioCommandBuilder.DecodedDigest(sourcePath, trim), cancellationToken);
            var output = await FfmpegStderr.RunForStdoutAsync(ffmpeg, KeptAudioCommandBuilder.DecodedDigest(outputPath, null), cancellationToken);
            return new(method,
                SourceDecodedDigest: source is null ? null : HashMuxerParser.Parse(source),
                OutputDecodedDigest: output is null ? null : HashMuxerParser.Parse(output));
        }

        var sourcePackets = await FfmpegStderr.RunForStdoutAsync(ffmpeg, KeptAudioCommandBuilder.PacketDigests(sourcePath), cancellationToken);
        var outputPackets = await FfmpegStderr.RunForStdoutAsync(ffmpeg, KeptAudioCommandBuilder.PacketDigests(outputPath), cancellationToken);
        return new(method,
            SourcePacketDigests: sourcePackets is null ? null : FrameHashParser.Parse(sourcePackets),
            OutputPacketDigests: outputPackets is null ? null : FrameHashParser.Parse(outputPackets));
    }
}
