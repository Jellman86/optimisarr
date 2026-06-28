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
    string? ImageScaleFilter = null,
    int? ClipSeconds = null,
    int? ClipStartSeconds = null);

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
    /// <param name="hardwareDecode">
    /// When <c>true</c> and a hardware (QSV/VAAPI) encoder is in use, the source is also
    /// decoded on the GPU (<c>-hwaccel</c>) so frames never round-trip through system memory —
    /// removing the software-decode CPU cost on large sources. Skipped when an HDR→SDR
    /// tone-map is requested, because that filter runs in software and needs frames in system
    /// memory. Not every source codec/profile can be hardware-decoded; the caller retries with
    /// this off when a hardware-decode attempt fails (see <see cref="HardwareDecodeFallback"/>).
    /// </param>
    public static IReadOnlyList<string> Build(
        TranscodeSpec spec,
        int threads = 0,
        string? videoEncoder = null,
        string? optimisedMarker = null,
        bool hardwareDecode = false)
    {
        var args = new List<string> { "-y" };

        if (threads > 0)
        {
            args.Add("-threads");
            args.Add(threads.ToString());
        }

        // Resolve the video encoder up front: a hardware encoder may need its device
        // initialised *before* the input (FFmpeg requires -vaapi_device / -init_hw_device
        // pre-input). Only a video re-encode (a non-null target codec) needs one.
        var isVideoReencode = spec.Kind is not (MediaKind.Audio or MediaKind.Image)
            && spec.VideoCodec is not null;
        var encoder = isVideoReencode ? (videoEncoder ?? EncoderFor(spec.VideoCodec!)) : null;
        var family = encoder is null ? EncoderFamily.Cpu : FamilyOf(encoder);

        // Hardware decode only makes sense alongside a hardware encoder, and only when no
        // software-only tone-map needs the frames in system memory.
        var useHardwareDecode = hardwareDecode
            && family is EncoderFamily.Qsv or EncoderFamily.Vaapi
            && !spec.TonemapToSdr;

        AppendHardwareDeviceInit(args, family, useHardwareDecode);

        // A preview clip seeks to its start before the input (fast keyframe seek) so the sample can
        // be taken from the middle of the file, where content is representative, not the intro.
        if (spec.ClipStartSeconds is { } start and > 0)
        {
            args.Add("-ss");
            args.Add(start.ToString());
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
                AppendVideoArguments(args, spec, encoder, family, useHardwareDecode);
                break;
        }

        // A preview clip limits the output to the first N seconds so a sample is fast to produce;
        // it is an output option, before the output path. Not used for full (replace-bound) jobs.
        if (spec.ClipSeconds is { } clip and > 0)
        {
            args.Add("-t");
            args.Add(clip.ToString());
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

    private static void AppendVideoArguments(
        List<string> args, TranscodeSpec spec, string? encoder, EncoderFamily family, bool hardwareDecode)
    {
        args.Add("-map");
        args.Add("0");

        // MP4/MOV cannot mux Matroska attachments (fonts/cover-art files) or data streams: ffmpeg
        // reports them as "codec none", fails to write the header, and aborts the whole job before a
        // single frame is produced. Exclude them for an MP4-family output so a source carrying one
        // still transcodes. Matroska holds them, so the blanket "-c copy" below keeps them there.
        if (IsMp4Family(spec.OutputPath))
        {
            args.Add("-map");
            args.Add("-0:t");
            args.Add("-map");
            args.Add("-0:d");
        }

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

        // One filter chain: optional HDR->SDR tone-map (in software), then any upload the
        // hardware encoder needs so it receives GPU surfaces. When the source is also being
        // hardware-decoded the frames are already GPU surfaces, so no upload is needed.
        var filters = new List<string>();
        if (spec.TonemapToSdr)
        {
            filters.Add(TonemapFilter);
        }
        if (!hardwareDecode)
        {
            switch (family)
            {
                case EncoderFamily.Vaapi:
                    filters.Add("format=nv12,hwupload");
                    break;
                case EncoderFamily.Qsv:
                    filters.Add("hwupload=extra_hw_frames=64,format=qsv");
                    break;
            }
        }
        // Copy every stream by default, then re-encode only the primary video (v:0). Embedded
        // cover-art / poster images (extra mjpeg/png video streams with an attached-pic disposition)
        // and any attachments/data thus stay copied: routing those tiny stills through a hardware
        // encoder fails with "Invalid argument" and aborts the whole job. Remuxes commonly carry
        // several such streams. Audio and subtitles below override this blanket copy as needed.
        args.Add("-c");
        args.Add("copy");

        if (filters.Count > 0)
        {
            // Filter only the primary video; a filtered stream cannot also be stream-copied, so
            // applying this to the cover-art streams would force them into the encoder too.
            args.Add("-filter:v:0");
            args.Add(string.Join(',', filters));
        }

        args.Add("-c:v:0");
        args.Add(encoder!);

        AppendQualityArguments(args, family, spec.Crf);

        // VAAPI encoders have no x264-style -preset; the others accept one when configured.
        if (family != EncoderFamily.Vaapi && !string.IsNullOrWhiteSpace(spec.Preset))
        {
            args.Add("-preset");
            args.Add(spec.Preset);
        }

        // MP4/MOV expect a constant frame rate: a variable-frame-rate source (whose timebase is not
        // cleanly divisible by the frame rate) drifts out of audio/video sync in the MP4 timeline,
        // which the A/V-sync verification gate then rejects. Normalise the re-encoded video to CFR for
        // MP4-family outputs only — Matroska carries VFR natively, so it is left untouched.
        if (IsMp4Family(spec.OutputPath))
        {
            args.Add("-fps_mode");
            args.Add("cfr");
        }

        // Audio is copied untouched unless the library opted into re-encoding it. MP4/MOV
        // cannot mux SubRip directly, so their text subtitles must use the native mov_text
        // codec; containers such as Matroska can retain the source subtitle codec unchanged.
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
        args.Add(IsMp4Family(spec.OutputPath) ? "mov_text" : "copy");
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

    // The hardware family is inferred from the resolved encoder name, so quality and device
    // arguments stay correct whatever codec was selected (e.g. h264_vaapi vs hevc_vaapi).
    private enum EncoderFamily { Cpu, Nvenc, Qsv, Vaapi }

    private static EncoderFamily FamilyOf(string encoder) =>
        encoder.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase) ? EncoderFamily.Nvenc
        : encoder.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase) ? EncoderFamily.Qsv
        : encoder.EndsWith("_vaapi", StringComparison.OrdinalIgnoreCase) ? EncoderFamily.Vaapi
        : EncoderFamily.Cpu;

    // VAAPI/QSV need a hardware device declared before the input. The render node is the
    // conventional default; CPU and NVENC (which encodes from software-decoded frames) need none.
    private const string DefaultRenderDevice = "/dev/dri/renderD128";

    private static void AppendHardwareDeviceInit(List<string> args, EncoderFamily family, bool hardwareDecode)
    {
        switch (family)
        {
            case EncoderFamily.Vaapi:
                args.Add("-vaapi_device");
                args.Add(DefaultRenderDevice);
                if (hardwareDecode)
                {
                    // Decode on the GPU and keep the frames there as VAAPI surfaces so the
                    // encoder consumes them directly (no software decode, no upload).
                    args.Add("-hwaccel");
                    args.Add("vaapi");
                    args.Add("-hwaccel_output_format");
                    args.Add("vaapi");
                }
                break;
            case EncoderFamily.Qsv:
                args.Add("-init_hw_device");
                args.Add("qsv=hw");
                args.Add("-filter_hw_device");
                args.Add("hw");
                if (hardwareDecode)
                {
                    // As above, but for QSV: decoded frames stay on the GPU as QSV surfaces.
                    args.Add("-hwaccel");
                    args.Add("qsv");
                    args.Add("-hwaccel_output_format");
                    args.Add("qsv");
                }
                break;
        }
    }

    // A single 0-51-ish quality knob per encoder family. Software x264/x265/SVT-AV1 take -crf;
    // the hardware encoders each name constant quality differently and reject -crf.
    private static void AppendQualityArguments(List<string> args, EncoderFamily family, int? crf)
    {
        if (crf is not { } quality)
        {
            return;
        }

        var q = quality.ToString();
        switch (family)
        {
            case EncoderFamily.Nvenc:
                // Constant-quality VBR with no target bitrate cap.
                args.AddRange(["-rc", "vbr", "-cq", q, "-b:v", "0"]);
                break;
            case EncoderFamily.Qsv:
                args.AddRange(["-global_quality", q]);
                break;
            case EncoderFamily.Vaapi:
                args.AddRange(["-rc_mode", "CQP", "-qp", q]);
                break;
            default:
                args.AddRange(["-crf", q]);
                break;
        }
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
