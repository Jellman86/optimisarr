using Optimisarr.Core;
using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Queue;

/// <summary>
/// A single transcode described in encoder-agnostic terms. For a video job
/// <see cref="VideoCodec"/> is <c>null</c> for a remux/cleanup (no re-encode); for an
/// audio job (<see cref="Kind"/> = <see cref="MediaKind.Audio"/>) the audio fields drive
/// the re-encode and the video fields are unused; for an image job
/// (<see cref="Kind"/> = <see cref="MediaKind.Image"/>) the image fields drive the re-encode.
/// </summary>
public sealed record TranscodeSpec(
    string InputPath,
    string OutputPath,
    string? VideoCodec,
    int? Crf,
    string? Preset,
    bool TonemapToSdr,
    MediaKind Kind = MediaKind.Video,
    string? AudioEncoder = null,
    int? AudioBitrateKbps = null,
    bool DownmixToStereo = false,
    string? ImageEncoder = null,
    int? ImageQuality = null,
    string? ImageScaleFilter = null);

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

        switch (spec.Kind)
        {
            case MediaKind.Audio:
                AppendAudioArguments(args, spec);
                break;
            case MediaKind.Image:
                AppendImageArguments(args, spec);
                break;
            default:
                AppendVideoArguments(args, spec, videoEncoder);
                break;
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

    private static void AppendVideoArguments(List<string> args, TranscodeSpec spec, string? videoEncoder)
    {
        args.Add("-map");
        args.Add("0");

        if (spec.VideoCodec is null)
        {
            // Remux only: copy every stream into the new container, no re-encode.
            args.Add("-c");
            args.Add("copy");

            // A library may still opt to shrink the audio; override the blanket copy for the
            // audio streams only, leaving video and subtitles untouched.
            if (spec.AudioEncoder is not null)
            {
                AppendAudioCodec(args, spec);
            }
            return;
        }

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

        // Audio is copied untouched unless the library opted into re-encoding it; subtitles
        // are always preserved.
        if (spec.AudioEncoder is not null)
        {
            AppendAudioCodec(args, spec);
        }
        else
        {
            args.Add("-c:a");
            args.Add("copy");
        }
        args.Add("-c:s");
        args.Add("copy");
    }

    private static void AppendAudioCodec(List<string> args, TranscodeSpec spec)
    {
        args.Add("-c:a");
        args.Add(spec.AudioEncoder!);

        if (spec.AudioBitrateKbps is { } bitrate)
        {
            args.Add("-b:a");
            args.Add($"{bitrate}k");
        }

        AppendDownmix(args, spec);
    }

    // A stereo downmix is only meaningful on a re-encode (a copied track keeps its layout),
    // so the resolver only sets the flag when audio is actually being re-encoded.
    private static void AppendDownmix(List<string> args, TranscodeSpec spec)
    {
        if (spec.DownmixToStereo)
        {
            args.Add("-ac");
            args.Add("2");
        }
    }

    private static void AppendAudioArguments(List<string> args, TranscodeSpec spec)
    {
        // Preserve all tags/metadata and re-encode only the audio. Any embedded cover art is
        // an attached-picture video stream, copied through untouched so album art survives.
        args.Add("-map_metadata");
        args.Add("0");

        args.Add("-map");
        args.Add("0:a");
        args.Add("-c:a");
        args.Add(spec.AudioEncoder ?? AudioTarget.Resolve(AudioTarget.DefaultCodec).Encoder);

        if (spec.AudioBitrateKbps is { } bitrate)
        {
            args.Add("-b:a");
            args.Add($"{bitrate}k");
        }

        AppendDownmix(args, spec);

        // "?" makes the cover-art stream optional so audio with no embedded art still works.
        args.Add("-map");
        args.Add("0:v?");
        args.Add("-c:v");
        args.Add("copy");
    }

    private static void AppendImageArguments(List<string> args, TranscodeSpec spec)
    {
        var encoder = spec.ImageEncoder ?? ImageTarget.Resolve(ImageTarget.DefaultFormat).Encoder;

        // Fail loudly before emitting a command for an unknown encoder, rather than producing a
        // malformed encode. Resolving the quality args up front validates the encoder is wired.
        var quality = spec.ImageQuality ?? ImageTarget.DefaultQuality;
        var qualityArgs = ImageQualityArguments(encoder, quality);

        // Carry the source image's EXIF/ICC profile and other metadata into the output. (Some
        // encoders, e.g. libwebp, drop it anyway; the portable marker is re-applied post-encode.)
        args.Add("-map_metadata");
        args.Add("0");

        // Take just the primary picture stream (an animated GIF is one multi-frame stream),
        // ignoring any embedded thumbnail.
        args.Add("-map");
        args.Add("0:v:0");

        // An optional downscale runs before the encoder; the resolver builds the scale expression.
        if (!string.IsNullOrWhiteSpace(spec.ImageScaleFilter))
        {
            args.Add("-vf");
            args.Add(spec.ImageScaleFilter);
        }

        args.Add("-c:v");
        args.Add(encoder);

        // A still is a single frame; tell the AV1 encoder so, and give it a 4:2:0 pixel format.
        if (encoder == "libaom-av1")
        {
            args.Add("-still-picture");
            args.Add("1");
            args.Add("-pix_fmt");
            args.Add("yuv420p");
        }

        args.AddRange(qualityArgs);
    }

    // Each still encoder names and scales its quality control differently. Optimisarr exposes a
    // single 0–100 quality (higher = better) per library; map it onto each encoder's native scale.
    private static IReadOnlyList<string> ImageQualityArguments(string encoder, int quality)
    {
        var q = Math.Clamp(quality, 0, 100);
        return encoder switch
        {
            // libwebp takes 0–100 directly (higher is better).
            "libwebp" => new[] { "-quality", q.ToString() },
            // mjpeg uses -q:v 2 (best) … 31 (worst); invert and scale our 0–100 onto that range.
            "mjpeg" => new[] { "-q:v", MapToRange(q, bestAt100: 2, worstAt0: 31).ToString() },
            // libaom-av1 still image uses constant-quality CRF 0 (best) … 63 (worst) with -b:v 0.
            "libaom-av1" => new[] { "-crf", MapToRange(q, bestAt100: 0, worstAt0: 63).ToString(), "-b:v", "0" },
            _ => throw new NotSupportedException(
                $"Image encoding for encoder '{encoder}' is not implemented yet.")
        };
    }

    // Linearly map a 0–100 quality (higher = better) onto an encoder scale where a lower number is
    // better: quality 100 → bestAt100, quality 0 → worstAt0.
    private static int MapToRange(int quality, int bestAt100, int worstAt0) =>
        (int)Math.Round(worstAt0 + (bestAt100 - worstAt0) * (quality / 100.0));

    private static bool IsMp4Family(string outputPath) =>
        // .m4a/.m4b are the MP4 audio containers (AAC target); they need the same flag for
        // the custom optimisation tag to survive.
        Path.GetExtension(outputPath).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov" or ".m4a" or ".m4b";

    private static string EncoderFor(string videoCodec) => videoCodec.Trim().ToLowerInvariant() switch
    {
        "hevc" or "h265" or "x265" => "libx265",
        "h264" or "avc" or "x264" => "libx264",
        "av1" => "libsvtav1",
        var other => throw new ArgumentOutOfRangeException(
            nameof(videoCodec), other, "No known ffmpeg encoder for this target codec.")
    };
}
