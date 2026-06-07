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

    public static IReadOnlyList<string> Build(TranscodeSpec spec)
    {
        var args = new List<string>
        {
            "-y",
            "-i", spec.InputPath,
            "-map", "0"
        };

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
            args.Add(EncoderFor(spec.VideoCodec));

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

        args.Add(spec.OutputPath);
        return args;
    }

    private static string EncoderFor(string videoCodec) => videoCodec.Trim().ToLowerInvariant() switch
    {
        "hevc" or "h265" or "x265" => "libx265",
        "h264" or "avc" or "x264" => "libx264",
        "av1" => "libsvtav1",
        var other => throw new ArgumentOutOfRangeException(
            nameof(videoCodec), other, "No known ffmpeg encoder for this target codec.")
    };
}
