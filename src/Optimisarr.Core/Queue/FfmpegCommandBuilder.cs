using Optimisarr.Core;

namespace Optimisarr.Core.Queue;

/// <summary>
/// A single transcode described in encoder-agnostic terms. <see cref="VideoCodec"/>
/// is <c>null</c> for a remux/cleanup (no re-encode).
/// </summary>
public sealed record TranscodeSpec(
    string InputPath,
    string OutputPath,
    string? VideoCodec,
    int? Crf,
    string? Preset,
    bool TonemapToSdr);

/// <summary>
/// Builds the ffmpeg argument list for a transcode. Returns a flat argument array
/// (never a shell string), so paths are passed verbatim and treated as untrusted
/// input — see the repository's safety standard. Pure and unit tested; it does not
/// run anything.
/// </summary>
public static class FfmpegCommandBuilder
{
    // A conservative Rec.709 tone-map chain for HDR (PQ/HLG) -> SDR.
    private const string TonemapFilter =
        "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable," +
        "zscale=t=bt709:m=bt709:r=tv,format=yuv420p";

    /// <param name="threads">
    /// CPU thread cap for encoding; <c>0</c> (or less) lets ffmpeg decide. Surfaced
    /// as a global option so it applies to a remux copy as well as a re-encode.
    /// </param>
    /// <param name="optimisedMarker">
    /// When set, written into the output's container metadata under
    /// <see cref="OptimisationMarker.MetadataKey"/> so the file proves it was optimised even
    /// if it is moved to another machine or the queue history is cleared. Applies to a remux
    /// copy as well as a re-encode.
    /// </param>
    public static IReadOnlyList<string> Build(
        TranscodeSpec spec, int threads = 0, string? videoEncoder = null, string? optimisedMarker = null)
    {
        var args = new List<string> { "-y" };

        if (threads > 0)
        {
            args.Add("-threads");
            args.Add(threads.ToString());
        }

        args.Add("-i");
        args.Add(spec.InputPath);
        args.Add("-map");
        args.Add("0");

        if (spec.VideoCodec is null)
        {
            // Remux only: copy every stream into the new container, no re-encode.
            args.Add("-c");
            args.Add("copy");
        }
        else
        {
            if (spec.TonemapToSdr)
            {
                args.Add("-vf");
                args.Add(TonemapFilter);
            }

            args.Add("-c:v");
            args.Add(videoEncoder ?? EncoderFor(spec.VideoCodec));

            if (spec.Crf is { } crf)
            {
                args.Add("-crf");
                args.Add(crf.ToString());
            }

            if (!string.IsNullOrWhiteSpace(spec.Preset))
            {
                args.Add("-preset");
                args.Add(spec.Preset);
            }

            // Audio and subtitles are preserved untouched in this phase.
            args.Add("-c:a");
            args.Add("copy");
            args.Add("-c:s");
            args.Add("copy");
        }

        if (!string.IsNullOrWhiteSpace(optimisedMarker))
        {
            args.Add("-metadata");
            args.Add($"{OptimisationMarker.MetadataKey}={optimisedMarker}");

            // The MP4/MOV muxer drops unrecognised metadata keys unless told to keep them;
            // Matroska and others preserve custom tags by default.
            if (IsMp4Family(spec.OutputPath))
            {
                args.Add("-movflags");
                args.Add("use_metadata_tags");
            }
        }

        args.Add(spec.OutputPath);
        return args;
    }

    private static bool IsMp4Family(string outputPath) =>
        Path.GetExtension(outputPath).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov";

    private static string EncoderFor(string videoCodec) => videoCodec.Trim().ToLowerInvariant() switch
    {
        "hevc" or "h265" or "x265" => "libx265",
        "h264" or "avc" or "x264" => "libx264",
        "av1" => "libsvtav1",
        var other => throw new ArgumentOutOfRangeException(
            nameof(videoCodec), other, "No known ffmpeg encoder for this target codec.")
    };
}
