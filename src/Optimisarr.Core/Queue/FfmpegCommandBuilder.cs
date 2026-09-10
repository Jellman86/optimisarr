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
    bool ImageLossless = false,
    bool SourceIsVariableFrameRate = false,
    int? ClipSeconds = null,
    int? ClipStartSeconds = null,
    // Audio-relative indexes of the source tracks a kept-languages rule removes from the
    // output (see AudioTrackSelection). Null or empty keeps every track.
    IReadOnlyList<int>? RemoveAudioStreamIndexes = null,
    // Subtitle-relative indexes of the source tracks the kept-languages rule removes
    // (see SubtitleTrackSelection). Null or empty keeps every track.
    IReadOnlyList<int>? RemoveSubtitleStreamIndexes = null,
    // Disposable video calibration candidates compare picture quality only. Excluding audio,
    // subtitles, attachments, and data keeps their timing and size out of that judgement.
    bool VideoOnly = false,
    // Portable advanced encoder intent (content tune, bitrate cap, adaptive quantisation).
    // Resolved onto this exact encoder's vocabulary by EncoderTuningPolicy, which drops anything
    // the chosen family cannot express rather than approximating it.
    EncoderTuning? Tuning = null,
    // The exact size a video re-encode scales to, or null for none. Computed once by the resolver
    // from the probed source so the filter emitted here and the verification gate share one
    // number rather than each rounding for themselves. Meaningless for a copied stream.
    PictureSize? DownscaleTo = null,
    // The picture to keep when black bars are removed, in source coordinates, or null for none.
    // Applied before any downscale, so a downscale is computed from the cropped size.
    CropRect? CropTo = null,
    // How a video re-encode thins its frames under a library's frame-rate cap, or null to keep
    // the source cadence. Always a clean halving of the source (see FrameRatePlanner), and the
    // same decimation the VMAF reference receives so the judged frames are the kept frames.
    FrameRateDecimation? FrameRate = null)
{
    /// <summary>The rate a capped encode produces, or null when the source cadence is kept.</summary>
    public double? TargetFrameRate => FrameRate?.TargetFps;

    /// <summary>
    /// The size this encode intends to produce, or null when it intends the source size. The
    /// verification gate holds the output to this. A downscale already accounts for any crop.
    /// </summary>
    public PictureSize? ExpectedSize =>
        DownscaleTo ?? (CropTo is { } crop ? new PictureSize(crop.Width, crop.Height) : null);
}

/// <summary>
/// Builds the ffmpeg argument list for a transcode. Returns a flat argument array
/// (never a shell string), so paths are passed verbatim and treated as untrusted
/// input — see the repository's safety standard. Pure and unit tested; it does not
/// run anything.
/// </summary>
public static class FfmpegCommandBuilder
{
    private const int ExactClipSeekDecodeSeconds = 10;

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
    /// When <c>true</c> and a hardware (NVENC/QSV/VAAPI) encoder is in use, the source is also
    /// decoded on the GPU (<c>-hwaccel</c>) so frames never round-trip through system memory —
    /// removing the software-decode CPU cost on large sources. Skipped when an HDR→SDR
    /// tone-map is requested, because that filter runs in software and needs frames in system
    /// memory. Not every source codec/profile can be hardware-decoded; the caller retries with
    /// this off when a hardware-decode attempt fails (see <see cref="HardwareDecodeFallback"/>).
    /// </param>
    /// <param name="hardwareToneMap">
    /// When <c>true</c>, an HDR-to-SDR QSV or VA-API job whose source is also hardware-decoded
    /// uses that family's VPP tone-map filter. Unsupported families and invalid combinations keep
    /// the established software filter; the caller provides a software command for runtime fallback.
    /// </param>
    public static IReadOnlyList<string> Build(
        TranscodeSpec spec,
        int threads = 0,
        string? videoEncoder = null,
        string? optimisedMarker = null,
        bool hardwareDecode = false,
        bool hardwareToneMap = false)
    {
        var args = new List<string>
        {
            "-y",
            // Human-readable stderr stats are presentation-oriented and their in-place line
            // framing varies by environment. The machine protocol is newline-delimited, stable,
            // and leaves stderr exclusively available for warnings and failure diagnostics.
            "-progress", "pipe:1",
            "-nostats"
        };

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

        var useHardwareToneMap = hardwareToneMap
            && hardwareDecode
            && spec.TonemapToSdr
            && family is EncoderFamily.Qsv or EncoderFamily.Vaapi;

        // A hardware tone-map consumes the decoded GPU surfaces directly. All other HDR-to-SDR
        // work retains the software colour pipeline and therefore needs system-memory frames.
        var useHardwareDecode = hardwareDecode
            && family is EncoderFamily.Nvenc or EncoderFamily.Qsv or EncoderFamily.Vaapi
            && (!spec.TonemapToSdr || useHardwareToneMap);

        AppendHardwareDeviceInit(args, family, useHardwareDecode);

        // Regenerate presentation timestamps for a video source whose DTS/PTS are missing or
        // non-monotonic, so it muxes cleanly instead of warning ("Non-monotonous DTS …") or aborting.
        // A demuxer input flag, so it precedes -i; a no-op when the source's timestamps are valid.
        if (spec.Kind is not (MediaKind.Audio or MediaKind.Image))
        {
            args.Add("-fflags");
            args.Add("+genpts");
        }

        var clipSeek = ResolveClipSeek(spec);
        if (clipSeek.InputStartSeconds is { } inputStart)
        {
            args.Add("-ss");
            args.Add(inputStart.ToString());
        }

        args.Add("-i");
        args.Add(spec.InputPath);

        if (clipSeek.OutputStartSeconds is { } outputStart)
        {
            args.Add("-ss");
            args.Add(outputStart.ToString());
        }

        switch (spec.Kind)
        {
            case MediaKind.Audio:
                AppendAudioArguments(args, spec);
                break;
            case MediaKind.Image:
                AppendImageArguments(args, spec);
                break;
            default:
                AppendVideoArguments(args, spec, encoder, family, useHardwareDecode, useHardwareToneMap);
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

    private static ClipSeek ResolveClipSeek(TranscodeSpec spec)
    {
        if (spec.ClipStartSeconds is not { } start || start <= 0)
        {
            return new ClipSeek(null, null);
        }

        if (spec.Kind != MediaKind.Video)
        {
            return new ClipSeek(start, null);
        }

        // Input seeking alone is accurate for the re-encoded picture but preserves keyframe
        // pre-roll for copied audio/subtitles. Decode at most a short lead-in, then apply an
        // output-side exact seek so every mapped stream starts on the same requested timeline.
        var inputStart = Math.Max(0, start - ExactClipSeekDecodeSeconds);
        return new ClipSeek(
            inputStart > 0 ? inputStart : null,
            start - inputStart);
    }

    private sealed record ClipSeek(int? InputStartSeconds, int? OutputStartSeconds);

    private static void AppendVideoArguments(
        List<string> args,
        TranscodeSpec spec,
        string? encoder,
        EncoderFamily family,
        bool hardwareDecode,
        bool hardwareToneMap)
    {
        args.Add("-map");
        args.Add(spec.VideoOnly ? "0:v:0" : "0");

        // MP4/MOV cannot mux Matroska attachments (fonts/cover-art files) or data streams: ffmpeg
        // reports them as "codec none", fails to write the header, and aborts the whole job before a
        // single frame is produced. Exclude them for an MP4-family output so a source carrying one
        // still transcodes. Matroska holds them, so the blanket "-c copy" below keeps them there.
        if (!spec.VideoOnly && IsMp4Family(spec.OutputPath))
        {
            args.Add("-map");
            args.Add("-0:t");
            args.Add("-map");
            args.Add("-0:d");
        }
        else if (!spec.VideoOnly && family is EncoderFamily.Qsv or EncoderFamily.Vaapi or EncoderFamily.Nvenc)
        {
            // A hardware encoder can abort on a data stream (camera timecode, GoPro GPMF) even in a
            // Matroska output, where the MP4 exclusion above doesn't apply. Drop data streams for any
            // hardware encode so such a source still transcodes; attachments stay (MKV holds them).
            args.Add("-map");
            args.Add("-0:d");
        }

        // The tracks a kept-languages rule removes. The selection already guarantees at least
        // one audio track survives, and the verification gate re-checks the output against the
        // planned removal — this only translates the decided indexes into stream exclusions.
        if (!spec.VideoOnly && spec.RemoveAudioStreamIndexes is { Count: > 0 } removedAudio)
        {
            foreach (var index in removedAudio)
            {
                args.Add("-map");
                args.Add($"-0:a:{index}");
            }
        }

        if (spec.RemoveSubtitleStreamIndexes is { Count: > 0 } removedSubtitles)
        {
            foreach (var index in removedSubtitles)
            {
                args.Add("-map");
                args.Add($"-0:s:{index}");
            }
        }

        if (spec.VideoCodec is null)
        {
            // Remux only: copy every stream into the new container, no re-encode.
            args.Add(spec.VideoOnly ? "-c:v" : "-c");
            args.Add("copy");

            // A library may still opt to shrink the audio; override the blanket copy for the
            // audio streams only, leaving video and subtitles untouched.
            if (!spec.VideoOnly && spec.AudioEncoder is not null)
            {
                AppendAudioCodec(args, spec);
            }
            return;
        }

        // One filter chain: optional downscale, optional HDR->SDR tone-map, then any upload the
        // hardware encoder needs. A supported hardware tone-map consumes the decoded GPU surfaces
        // directly. The downscale goes first: it is a software filter so it must precede any
        // upload, and scaling before the tone-map does the expensive colour work on fewer pixels.
        var filters = new List<string>();
        // Crop first, then scale: the downscale was computed from the cropped size, and scaling
        // bars only to cut them away afterwards would waste the work and blur the edge.
        if (spec.CropTo is { } crop)
        {
            filters.Add(CropPlanner.Filter(crop));
        }
        if (spec.DownscaleTo is { } downscale)
        {
            filters.Add(PictureGeometry.ScaleFilter(downscale));
        }
        // Decimate after the geometry and before the tone-map, so the expensive colour work runs
        // only on the frames that survive.
        if (spec.FrameRate is { } decimation)
        {
            filters.Add(FrameRatePlanner.Filter(decimation));
        }
        if (spec.TonemapToSdr)
        {
            filters.Add(hardwareToneMap
                ? family == EncoderFamily.Qsv
                    ? HdrToneMap.QsvFilter
                    : HdrToneMap.VaapiFilter
                : HdrToneMap.SoftwareFilter);
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
        if (!spec.VideoOnly)
        {
            args.Add("-c");
            args.Add("copy");
        }

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

        // The dispatcher has already resolved the portable effort onto this exact encoder's
        // vocabulary. VAAPI and VideoToolbox have no cross-codec equivalent and receive no preset.
        if (family is not (EncoderFamily.Vaapi or EncoderFamily.VideoToolbox)
            && !string.IsNullOrWhiteSpace(spec.Preset))
        {
            args.Add("-preset");
            args.Add(spec.Preset);
        }

        // Advanced encoder options, resolved for this exact encoder. A family with no equivalent
        // contributes nothing here, so an Auto-mode library that lands on QSV simply encodes as it
        // always did rather than receiving arguments it would reject.
        if (spec.Tuning is { IsEmpty: false } tuning && encoder is not null)
        {
            args.AddRange(EncoderTuningPolicy.Resolve(encoder, tuning));
        }

        // Preserve a source that ffprobe positively identified as VFR. MP4 supports variable frame
        // durations; forcing CFR duplicates/drops frames and changes motion cadence. Demux timebase
        // keeps encoder timestamps anchored to the source. CFR and unknown sources need no override.
        // A frame-rate target replaces the source cadence with a regular one through the fps
        // filter; asking the encoder to also preserve the original timing would contradict it.
        if (spec.SourceIsVariableFrameRate && spec.TargetFrameRate is null)
        {
            args.Add("-fps_mode");
            args.Add("vfr");
            args.Add("-enc_time_base:v:0");
            args.Add("demux");
        }

        // Audio is copied untouched unless the library opted into re-encoding it. MP4/MOV
        // cannot mux SubRip directly, so their text subtitles must use the native mov_text
        // codec; containers such as Matroska can retain the source subtitle codec unchanged.
        if (spec.VideoOnly)
        {
            return;
        }

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

        // The final-image smoke test proves MP3/APIC artwork retention with the shipped FFmpeg.
        // AAC and Opus candidates with attached pictures are rejected before dispatch because
        // their muxers cannot safely translate inherited FLAC/MP3 picture streams in this build.
        // Map MP3's picture before the audio and target its output stream explicitly.
        if (Path.GetExtension(spec.OutputPath).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-map");
            args.Add("0:v?");
            args.Add("-c:v:0");
            // Normalise artwork to the universally supported APIC/MP4 cover codec so both JPEG
            // and PNG source pictures have one deterministic representation across players.
            args.Add("mjpeg");
            args.Add("-disposition:v:0");
            args.Add("attached_pic");
        }

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

        // M4A can retain a timed-lyrics/subtitle stream as mov_text. The other supported audio
        // containers cannot, and their candidates are rejected when such a stream exists.
        if (Path.GetExtension(spec.OutputPath).Equals(".m4a", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-map");
            args.Add("0:s?");
            args.Add("-c:s");
            args.Add("mov_text");
        }
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

        if (encoder == "libwebp" && spec.ImageLossless)
        {
            args.Add("-lossless");
            args.Add("1");
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
    private enum EncoderFamily { Cpu, Nvenc, Qsv, Vaapi, VideoToolbox }

    private static EncoderFamily FamilyOf(string encoder) =>
        encoder.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase) ? EncoderFamily.Nvenc
        : encoder.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase) ? EncoderFamily.Qsv
        : encoder.EndsWith("_vaapi", StringComparison.OrdinalIgnoreCase) ? EncoderFamily.Vaapi
        : encoder.EndsWith("_videotoolbox", StringComparison.OrdinalIgnoreCase) ? EncoderFamily.VideoToolbox
        : EncoderFamily.Cpu;

    // Apple's encoders take constant quality as -q:v on a 1–100 scale where higher is better, the
    // inverse of CRF. A straight line through the two ranges keeps the operator's number meaning
    // "lower is better" everywhere else; the VMAF gate, not this mapping, is what guarantees the
    // result, and real-hardware calibration of the line is still owed (see the roadmap).
    private static int VideoToolboxQuality(int crf) => Math.Clamp(100 - 2 * crf, 1, 100);

    // VAAPI/QSV need a hardware device declared before the input. The render node is the
    // conventional default; CUDA uses the first GPU exposed to the container.
    private const string DefaultRenderDevice = "/dev/dri/renderD128";

    private static void AppendHardwareDeviceInit(List<string> args, EncoderFamily family, bool hardwareDecode)
    {
        switch (family)
        {
            case EncoderFamily.Nvenc when hardwareDecode:
                // Let FFmpeg select the source codec's NVDEC implementation and keep its CUDA
                // frames on the GPU for NVENC. This is the NVIDIA-documented zero-copy path;
                // forcing a *_cuvid decoder would unnecessarily couple the command to the codec.
                args.Add("-hwaccel");
                args.Add("cuda");
                args.Add("-hwaccel_output_format");
                args.Add("cuda");
                break;
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
            case EncoderFamily.VideoToolbox:
                args.AddRange(["-q:v", VideoToolboxQuality(quality).ToString()]);
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
