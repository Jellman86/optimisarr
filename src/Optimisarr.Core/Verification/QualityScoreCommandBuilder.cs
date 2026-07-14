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
    VmafAcceleration Acceleration = VmafAcceleration.None);

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

        // The established HDR path uses software zscale/tonemap and preserves 10-bit frames.
        // None of the accelerated graphs can reproduce that preparation exactly, so correctness
        // takes priority over speed for HDR material.
        var acceleration = context.ReferenceIsHdr ? VmafAcceleration.None : context.Acceleration;

        // Cropped cinema masters are commonly 3840x1600-ish while still intended
        // for a 4K display, so either UHD axis selects the 4K viewing model.
        var model = context.ReferenceWidth >= 3840 || context.ReferenceHeight >= 2160
            ? UhdModelVersion
            : HdModelVersion;
        var colourPreprocessing = context.ReferenceIsHdr
            ? context.HdrConvertedToSdr
                ? "HDR reference tone-mapped to SDR"
                : "HDR (matching transfer characteristics)"
            : "SDR";
        var preprocessing = DescribePreprocessing(
            colourPreprocessing,
            acceleration,
            context.FrameSubsample);
        var scale =
            $"scale={context.ReferenceWidth}:{context.ReferenceHeight}:" +
            "flags=bicubic:in_range=auto:out_range=tv";
        var pixelFormat = context.ReferenceIsHdr && !context.HdrConvertedToSdr
            ? "yuv420p10le"
            : "yuv420p";
        var normalise = $"{scale},format={pixelFormat}";
        var referencePreparation = context.ReferenceIsHdr && context.HdrConvertedToSdr
            ? $"{HdrToneMap.Filter},{normalise}"
            : normalise;
        var boundedThreads = Math.Max(1, threads);
        var filter = acceleration == VmafAcceleration.Cuda
            ? BuildCudaFilter(context, logPath, model, boundedThreads)
            : BuildCpuFilter(
                normalise,
                referencePreparation,
                logPath,
                model,
                boundedThreads,
                context.FrameSubsample,
                acceleration);

        var arguments = new List<string>
        {
            "-nostdin",
            "-v", "error",
            // -stats forces ffmpeg to print per-frame "time=" progress to stderr even at the error
            // log level, so verification can report real progress without any other noise.
            "-stats",
        };
        AppendDeviceInitialisation(arguments, acceleration);
        // Clip-VMAF seeks the distorted (output) input into the same window as the reference so a
        // full-file job can score only a representative segment. An accurate seek before -i decodes
        // to the point, keeping the two streams frame-aligned.
        if (context.DistortedStartSeconds is > 0)
        {
            arguments.Add("-ss");
            arguments.Add(context.DistortedStartSeconds.Value.ToString());
        }
        AppendInputAcceleration(arguments, acceleration);
        // libvmaf requires distorted first and reference second.
        arguments.Add("-i");
        arguments.Add(distortedPath);
        // Preview outputs begin at zero after an accurate decode seek into the source. Seek the
        // full reference as its own decoded input so FFmpeg discards keyframe pre-roll before
        // libvmaf; comparing against a stream-copied clip can start on an earlier keyframe.
        if (context.ReferenceStartSeconds is > 0)
        {
            arguments.Add("-ss");
            arguments.Add(context.ReferenceStartSeconds.Value.ToString());
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
        string normalise,
        string referencePreparation,
        string logPath,
        string model,
        int threads,
        int frameSubsample,
        VmafAcceleration acceleration)
    {
        var download = acceleration is VmafAcceleration.Qsv or VmafAcceleration.Vaapi
            ? "hwdownload,format=nv12,"
            : string.Empty;
        return
            $"[0:v]{download}settb=AVTB,setpts=PTS-STARTPTS,{normalise}[dist];" +
            $"[1:v]{download}settb=AVTB,setpts=PTS-STARTPTS,{referencePreparation}[ref];" +
            "[dist][ref]libvmaf=" +
            $"model=version={model}:" +
            $"n_threads={threads}:n_subsample={frameSubsample}:" +
            $"log_fmt=json:log_path={logPath}:shortest=1:repeatlast=0";
    }

    private static string BuildCudaFilter(
        QualityMeasurementContext context,
        string logPath,
        string model,
        int threads)
    {
        var scale =
            $"scale_cuda={context.ReferenceWidth}:{context.ReferenceHeight}:" +
            "interp_algo=bicubic:format=yuv420p";
        return
            $"[0:v]settb=AVTB,setpts=PTS-STARTPTS,{scale}[dist];" +
            $"[1:v]settb=AVTB,setpts=PTS-STARTPTS,{scale}[ref];" +
            "[dist][ref]libvmaf_cuda=" +
            $"model=version={model}:" +
            $"n_threads={threads}:n_subsample={context.FrameSubsample}:" +
            $"log_fmt=json:log_path={logPath}:shortest=1:repeatlast=0";
    }

    private static string DescribePreprocessing(
        string colourPreprocessing,
        VmafAcceleration acceleration,
        int frameSubsample)
    {
        var hardware = acceleration switch
        {
            VmafAcceleration.Cuda => "CUDA VMAF",
            VmafAcceleration.Qsv => "QSV decode + CPU VMAF",
            VmafAcceleration.Vaapi => "VA-API decode + CPU VMAF",
            _ => null
        };
        var sampling = frameSubsample > 1 ? $"every {frameSubsample}th frame" : null;
        return string.Join(" · ", new[] { colourPreprocessing, hardware, sampling }
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
