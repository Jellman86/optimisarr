using System.Globalization;
using Optimisarr.Core.Queue;

namespace Optimisarr.Core.Verification;

/// <summary>The optional hardware path requested for a VMAF measurement.</summary>
public enum VmafAcceleration
{
    None,
    Cuda,
    Qsv,
    Vaapi
}

/// <summary>Maps the encoder already selected for a job onto its compatible VMAF decode path.</summary>
public static class VmafAccelerationSelector
{
    public static VmafAcceleration Select(string? encoder, bool hardwareDecodeEnabled)
    {
        if (!hardwareDecodeEnabled || string.IsNullOrWhiteSpace(encoder))
        {
            return VmafAcceleration.None;
        }

        return encoder.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase) ? VmafAcceleration.Cuda
            : encoder.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase) ? VmafAcceleration.Qsv
            : encoder.EndsWith("_vaapi", StringComparison.OrdinalIgnoreCase) ? VmafAcceleration.Vaapi
            : VmafAcceleration.None;
    }
}

/// <summary>The source characteristics needed to make a like-for-like VMAF comparison.</summary>
public sealed record QualityMeasurementContext(
    int ReferenceWidth,
    int ReferenceHeight,
    bool ReferenceIsHdr,
    bool HdrConvertedToSdr,
    int? ReferenceStartSeconds = null,
    double? ReferenceDurationSeconds = null,
    // Clip-VMAF: seek the distorted (output) input too, and cap the measurement to a window, so a
    // full-file job can score just a representative segment. ReferenceStartSeconds carries the same
    // seek for the reference input.
    int? DistortedStartSeconds = null,
    int? MeasureDurationSeconds = null,
    int FrameSubsample = 1,
    VmafAcceleration Acceleration = VmafAcceleration.None,
    double? ReferenceFrameRate = null,
    // The crop the encode applied, so the reference is cropped identically before comparison. A
    // cropped output measured against an uncropped reference is comparing different pictures.
    // The comparison then happens at the cropped size.
    Queue.CropRect? ReferenceCrop = null,
    // How a capped encode thinned its frames, so the reference is thinned by the same index rule
    // before any timestamp handling. Decimating by nearest timestamp instead can keep different
    // frames than the encode kept, and then the comparison is of neighbours, not of the same frame.
    Queue.FrameRateDecimation? ReferenceDecimation = null);

/// <summary>A complete, shell-free FFmpeg VMAF invocation and its selected measurement policy.</summary>
public sealed record QualityScoreCommand(
    IReadOnlyList<string> Arguments,
    string FilterGraph,
    string ModelVersion,
    string Preprocessing);

/// <summary>
/// Builds Optimisarr's canonical VMAF command. Selection is automatic: UHD uses
/// Netflix's 4K model, other sources use the default HDTV model, and a reference
/// is prepared in the same SDR domain when the encode intentionally tone-mapped
/// HDR. Both streams receive a common timebase, range and reference resolution.
/// </summary>
public static class QualityScoreCommandBuilder
{
    public const string HdModelVersion = "vmaf_v0.6.1";
    public const string UhdModelVersion = "vmaf_4k_v0.6.1";
    public const int MaximumFrameSubsample = 10;

    /// <summary>
    /// The viewing model for a picture of this size. Cropped cinema masters are commonly
    /// 3840x1600-ish while still intended for a 4K display, so either UHD axis selects the 4K
    /// model. Public so an assignment can tell a remote worker which model its evidence must name.
    /// </summary>
    public static string ModelVersionFor(int referenceWidth, int referenceHeight) =>
        referenceWidth >= 3840 || referenceHeight >= 2160 ? UhdModelVersion : HdModelVersion;
    private const int SampleSeekPrerollSeconds = 5;
    private const string DefaultRenderDevice = "/dev/dri/renderD128";

    public static QualityScoreCommand Build(
        string distortedPath,
        string referencePath,
        string logPath,
        QualityMeasurementContext context,
        int threads)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distortedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(referencePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(context.ReferenceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(context.ReferenceHeight);
        if (context.FrameSubsample is < 1 or > MaximumFrameSubsample)
        {
            throw new ArgumentOutOfRangeException(
                nameof(context),
                $"VMAF frame subsampling must be between 1 and {MaximumFrameSubsample}.");
        }
        if (context.ReferenceFrameRate is { } frameRate
            && (!double.IsFinite(frameRate) || frameRate <= 0 || frameRate > 1_000))
        {
            throw new ArgumentOutOfRangeException(
                nameof(context),
                "VMAF reference frame rate must be finite and between 0 and 1000 fps.");
        }

        // The established HDR path uses software zscale/tonemap and preserves 10-bit frames.
        // None of the accelerated graphs can reproduce that preparation exactly, so correctness
        // takes priority over speed for HDR material.
        // A crop is a software filter with no CUDA counterpart in the accelerated graph, so a
        // cropped comparison stays on the CPU path — the same choice HDR makes, for the same
        // reason: correctness over speed.
        // A decimated reference likewise: the frame selection must be reproduced exactly, and
        // only the CPU graph carries it.
        var acceleration = context.ReferenceIsHdr
            || context.ReferenceCrop is not null
            || context.ReferenceDecimation is not null
            ? VmafAcceleration.None
            : context.Acceleration;

        // Thinning by frame index happens before anything touches timestamps, so the index each
        // frame is judged by is the one the encode judged it by.
        var referenceDecimation = context.ReferenceDecimation is { } decimation
            ? $"{Queue.FrameRatePlanner.Filter(decimation)},"
            : string.Empty;

        // With a crop, the picture being judged is the cropped one: both streams are brought to
        // its size, and the viewing model is chosen from it.
        var referenceWidth = context.ReferenceCrop?.Width ?? context.ReferenceWidth;
        var referenceHeight = context.ReferenceCrop?.Height ?? context.ReferenceHeight;

        var model = ModelVersionFor(referenceWidth, referenceHeight);
        var colourPreprocessing = context.ReferenceIsHdr
            ? context.HdrConvertedToSdr
                ? "HDR reference tone-mapped to SDR"
                : "HDR (matching transfer characteristics)"
            : "SDR";
        var preprocessing = DescribePreprocessing(
            colourPreprocessing,
            acceleration,
            context.FrameSubsample,
            context.ReferenceFrameRate);
        var scale =
            $"scale={referenceWidth}:{referenceHeight}:" +
            "flags=bicubic:in_range=auto:out_range=tv";
        var pixelFormat = context.ReferenceIsHdr && !context.HdrConvertedToSdr
            ? "yuv420p10le"
            : "yuv420p";
        var distortedInputStart = InputSeek(context.DistortedStartSeconds, context.MeasureDurationSeconds);
        var referenceInputStart = InputSeek(context.ReferenceStartSeconds, context.MeasureDurationSeconds);
        var distortedTimeline = TimelinePreparation(
            context.DistortedStartSeconds,
            distortedInputStart,
            context.MeasureDurationSeconds,
            context.ReferenceFrameRate);
        var referenceTimeline = TimelinePreparation(
            context.ReferenceStartSeconds,
            referenceInputStart,
            context.MeasureDurationSeconds,
            context.ReferenceFrameRate);
        var normalise = $"{scale},format={pixelFormat}";
        var referencePreparation = context.ReferenceIsHdr && context.HdrConvertedToSdr
            ? $"{HdrToneMap.Filter},{normalise}"
            : normalise;
        if (context.ReferenceCrop is { } referenceCrop)
        {
            // The output is already cropped; only the reference needs it, and before anything else.
            referencePreparation = $"{Queue.CropPlanner.Filter(referenceCrop)},{referencePreparation}";
        }
        var boundedThreads = Math.Max(1, threads);
        var filter = acceleration == VmafAcceleration.Cuda
            ? BuildCudaFilter(
                context,
                logPath,
                model,
                boundedThreads,
                distortedTimeline,
                referenceTimeline)
            : BuildCpuFilter(
                referenceDecimation,
                normalise,
                referencePreparation,
                logPath,
                model,
                boundedThreads,
                context.FrameSubsample,
                acceleration,
                distortedTimeline,
                referenceTimeline);

        var arguments = new List<string>
        {
            "-nostdin",
            "-v", "error",
            // -stats forces ffmpeg to print per-frame "time=" progress to stderr even at the error
            // log level, so verification can report real progress without any other noise.
            "-stats",
        };
        AppendDeviceInitialisation(arguments, acceleration);
        // Sampled VMAF seeks both independently encoded inputs just before the requested window.
        // The filter graph trims matching decoded pre-roll so different GOP layouts and decoder
        // startup frames cannot contaminate the scored interval.
        if (distortedInputStart is > 0)
        {
            arguments.Add("-ss");
            arguments.Add(distortedInputStart.Value.ToString());
        }
        AppendInputAcceleration(arguments, acceleration);
        // libvmaf requires distorted first and reference second.
        arguments.Add("-i");
        arguments.Add(distortedPath);
        // Preview outputs begin at zero after an accurate decode seek into the source. Seek the
        // full reference as its own decoded input so FFmpeg discards keyframe pre-roll before
        // libvmaf; comparing against a stream-copied clip can start on an earlier keyframe.
        if (referenceInputStart is > 0)
        {
            arguments.Add("-ss");
            arguments.Add(referenceInputStart.Value.ToString());
        }
        AppendInputAcceleration(arguments, acceleration);
        arguments.Add("-i");
        arguments.Add(referencePath);
        arguments.AddRange(["-lavfi", filter]);
        // Cap the measurement to the clip length (clip-VMAF); without it the whole file is scored.
        if (context.MeasureDurationSeconds is > 0)
        {
            arguments.Add("-t");
            arguments.Add(context.MeasureDurationSeconds.Value.ToString());
        }
        arguments.AddRange(["-f", "null", "-"]);

        return new QualityScoreCommand(arguments, filter, model, preprocessing);
    }

    private static string BuildCpuFilter(
        string referenceDecimation,
        string normalise,
        string referencePreparation,
        string logPath,
        string model,
        int threads,
        int frameSubsample,
        VmafAcceleration acceleration,
        string distortedTimeline,
        string referenceTimeline)
    {
        var download = acceleration is VmafAcceleration.Qsv or VmafAcceleration.Vaapi
            ? "hwdownload,format=nv12,"
            : string.Empty;
        return
            $"[0:v]{download}{distortedTimeline},{normalise}[dist];" +
            $"[1:v]{download}{referenceDecimation}{referenceTimeline},{referencePreparation}[ref];" +
            "[dist][ref]libvmaf=" +
            $"model=version={model}:" +
            $"n_threads={threads}:n_subsample={frameSubsample}:" +
            $"log_fmt=json:log_path={logPath}:shortest=1:repeatlast=0";
    }

    private static string BuildCudaFilter(
        QualityMeasurementContext context,
        string logPath,
        string model,
        int threads,
        string distortedTimeline,
        string referenceTimeline)
    {
        var scale =
            $"scale_cuda={context.ReferenceWidth}:{context.ReferenceHeight}:" +
            "interp_algo=bicubic:format=yuv420p";
        return
            $"[0:v]{distortedTimeline},{scale}[dist];" +
            $"[1:v]{referenceTimeline},{scale}[ref];" +
            "[dist][ref]libvmaf_cuda=" +
            $"model=version={model}:" +
            $"n_threads={threads}:n_subsample={context.FrameSubsample}:" +
            $"log_fmt=json:log_path={logPath}:shortest=1:repeatlast=0";
    }

    private static int? InputSeek(int? windowStartSeconds, int? windowDurationSeconds) =>
        windowStartSeconds is not { } start
            ? null
            : windowDurationSeconds is > 0
                ? Math.Max(0, start - SampleSeekPrerollSeconds)
                : start;

    private static string TimelinePreparation(
        int? windowStartSeconds,
        int? inputStartSeconds,
        int? windowDurationSeconds,
        double? referenceFrameRate)
    {
        // An input seek leaves each decoder's first retained PTS relative to the common
        // pre-roll target. Different GOP layouts can therefore begin at different positive PTS
        // values; those offsets identify the same presentation instant and must survive until fps
        // puts both streams on one cadence. Full-file inputs have no shared seek target, so rebase
        // their container origins first. The final reset leaves libvmaf with zero-based timelines.
        const string origin = "settb=AVTB,setpts=PTS-STARTPTS";
        var inputTimeline = inputStartSeconds is > 0 ? "settb=AVTB" : origin;
        var cadence = referenceFrameRate is { } frameRate
            ? $"fps=fps={frameRate.ToString("G17", CultureInfo.InvariantCulture)}:start_time=0,"
            : string.Empty;
        var alignment = windowStartSeconds is { } start && windowDurationSeconds is > 0
            ? $"trim=start={start - (inputStartSeconds ?? 0)}:duration={windowDurationSeconds.Value},"
            : string.Empty;
        return cadence.Length == 0 && alignment.Length == 0
            ? origin
            : $"{inputTimeline},{cadence}{alignment}{origin}";
    }

    private static string DescribePreprocessing(
        string colourPreprocessing,
        VmafAcceleration acceleration,
        int frameSubsample,
        double? referenceFrameRate)
    {
        var hardware = acceleration switch
        {
            VmafAcceleration.Cuda => "CUDA VMAF",
            VmafAcceleration.Qsv => "QSV decode + CPU VMAF",
            VmafAcceleration.Vaapi => "VA-API decode + CPU VMAF",
            _ => null
        };
        var cadence = referenceFrameRate is { } frameRate
            ? $"{frameRate.ToString("0.###", CultureInfo.InvariantCulture)} fps aligned"
            : null;
        var sampling = frameSubsample > 1 ? $"every {frameSubsample}th frame" : null;
        return string.Join(" · ", new[] { colourPreprocessing, hardware, cadence, sampling }
            .Where(part => part is not null));
    }

    private static void AppendDeviceInitialisation(List<string> arguments, VmafAcceleration acceleration)
    {
        switch (acceleration)
        {
            case VmafAcceleration.Qsv:
                arguments.AddRange(["-init_hw_device", "qsv=hw", "-filter_hw_device", "hw"]);
                break;
            case VmafAcceleration.Vaapi:
                arguments.AddRange(["-vaapi_device", DefaultRenderDevice]);
                break;
        }
    }

    private static void AppendInputAcceleration(List<string> arguments, VmafAcceleration acceleration)
    {
        switch (acceleration)
        {
            case VmafAcceleration.Cuda:
                arguments.AddRange(["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"]);
                break;
            case VmafAcceleration.Qsv:
                arguments.AddRange([
                    "-hwaccel", "qsv",
                    "-hwaccel_output_format", "qsv",
                    "-hwaccel_device", "hw"]);
                break;
            case VmafAcceleration.Vaapi:
                arguments.AddRange([
                    "-hwaccel", "vaapi",
                    "-hwaccel_output_format", "vaapi",
                    "-hwaccel_device", DefaultRenderDevice]);
                break;
        }
    }
}
