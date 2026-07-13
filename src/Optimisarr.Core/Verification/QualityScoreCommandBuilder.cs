using Optimisarr.Core.Queue;

namespace Optimisarr.Core.Verification;

/// <summary>The source characteristics needed to make a like-for-like VMAF comparison.</summary>
public sealed record QualityMeasurementContext(
    int ReferenceWidth,
    int ReferenceHeight,
    bool ReferenceIsHdr,
    bool HdrConvertedToSdr,
    int? ReferenceStartSeconds = null);

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

        // Cropped cinema masters are commonly 3840x1600-ish while still intended
        // for a 4K display, so either UHD axis selects the 4K viewing model.
        var model = context.ReferenceWidth >= 3840 || context.ReferenceHeight >= 2160
            ? UhdModelVersion
            : HdModelVersion;
        var preprocessing = context.ReferenceIsHdr
            ? context.HdrConvertedToSdr
                ? "HDR reference tone-mapped to SDR"
                : "HDR (matching transfer characteristics)"
            : "SDR";
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
        var filter =
            $"[0:v]settb=AVTB,setpts=PTS-STARTPTS,{normalise}[dist];" +
            $"[1:v]settb=AVTB,setpts=PTS-STARTPTS,{referencePreparation}[ref];" +
            "[dist][ref]libvmaf=" +
            $"model=version={model}:" +
            "feature=name=psnr\\|name=float_ssim:" +
            $"n_threads={boundedThreads}:n_subsample=1:" +
            $"log_fmt=json:log_path={logPath}:shortest=1:repeatlast=0";

        var arguments = new List<string>
        {
            "-nostdin",
            "-v", "error",
            // libvmaf requires distorted first and reference second.
            "-i", distortedPath
        };
        // Preview outputs begin at zero after an accurate decode seek into the source. Seek the
        // full reference as its own decoded input so FFmpeg discards keyframe pre-roll before
        // libvmaf; comparing against a stream-copied clip can start on an earlier keyframe.
        if (context.ReferenceStartSeconds is > 0)
        {
            arguments.Add("-ss");
            arguments.Add(context.ReferenceStartSeconds.Value.ToString());
        }
        arguments.Add("-i");
        arguments.Add(referencePath);
        arguments.AddRange(
        [
            "-lavfi", filter,
            "-f", "null",
            "-"
        ]);

        return new QualityScoreCommand(arguments, filter, model, preprocessing);
    }
}
