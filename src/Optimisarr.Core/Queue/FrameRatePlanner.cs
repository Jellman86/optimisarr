using System.Globalization;

namespace Optimisarr.Core.Queue;

/// <summary>
/// How a capped encode thins its source: keep one frame in every <see cref="Divisor"/>, judged by
/// each source frame's index. The same three numbers build the encode filter and the VMAF
/// reference filter, so the frames judged are the frames kept.
/// </summary>
public sealed record FrameRateDecimation(double SourceFps, double TargetFps, int Divisor);

/// <summary>
/// Turns a library's frame-rate cap into an exact decimation for one source, or into nothing.
///
/// The only conversion that is ever clean is dropping every other frame — an integer ratio. Any
/// other ratio (30→25, 60→24) repeats or skips a frame every few and judders, which is a worse
/// picture than either rate. So the plan halves the source rate until it sits under the cap and
/// refuses anything that cannot be reached that way.
///
/// Frames are chosen by index, not by nearest timestamp. FFmpeg's <c>fps</c> filter decides each
/// output slot by rounding, and on an exact 2:1 the odd frames sit precisely on the rounding
/// boundary, so which of two source frames it keeps depends on the stream's timebase and on
/// whether timestamps were rebased first. The first real capped encode and its verification made
/// different choices for half the frames and scored VMAF 48 for a 97 encode. An index has no
/// boundary to fall on.
/// </summary>
public static class FrameRatePlanner
{
    /// <summary>
    /// Below this the picture stops reading as motion. A halving that lands under it (45 → 22.5)
    /// has no clean answer, so the source is left alone rather than made worse.
    /// </summary>
    public const double MinimumTargetFps = 20;

    /// <summary>
    /// The decimation that brings <paramref name="sourceFps"/> under <paramref name="capFps"/>, or
    /// <c>null</c> when the source is already at or under the cap, when either value is unknown,
    /// when no clean halving reaches the cap without falling below cinema rate, or when the source
    /// is variable frame rate — an irregular stream has no "every second frame" that means the
    /// same thing to the encode and to the reference.
    /// </summary>
    public static FrameRateDecimation? Plan(double? sourceFps, int? capFps, bool sourceIsVariableFrameRate = false)
    {
        if (sourceIsVariableFrameRate
            || sourceFps is not { } source || capFps is not { } cap
            || !double.IsFinite(source) || source <= 0 || cap <= 0)
        {
            return null;
        }

        // Probed rates carry rounding (59.94 arrives as 59.940059...); a source that is the cap to
        // within a frame per thousand is at the cap, not above it.
        if (source <= cap * (1 + Tolerance))
        {
            return null;
        }

        var target = source;
        var divisor = 1;
        while (target > cap * (1 + Tolerance))
        {
            target /= 2;
            divisor *= 2;
        }

        return target >= MinimumTargetFps ? new FrameRateDecimation(source, target, divisor) : null;
    }

    /// <summary>
    /// The FFmpeg filter that keeps one source frame in every <see cref="FrameRateDecimation.Divisor"/>.
    /// Each frame's index is recovered from its own timestamp, <c>round(t × source rate)</c>, so the
    /// choice is the same whether the stream was seeked, rebased or rescaled first: container
    /// timestamp jitter is well under half a frame, and the source rate is the probe's exact
    /// rational, so the rounding is never near a boundary. Kept frames keep their own timestamps,
    /// which are already regular at the target rate.
    /// </summary>
    public static string Filter(FrameRateDecimation decimation) =>
        $"select=not(mod(round(t*{decimation.SourceFps.ToString("R", CultureInfo.InvariantCulture)})\\,{decimation.Divisor}))";

    /// <summary>
    /// How many frames survive a decimation from <paramref name="sourceFps"/> to
    /// <paramref name="targetFps"/>, for progress estimation. Unknown rates leave the count alone.
    /// </summary>
    public static int? ScaleFrameCount(int? frames, double? sourceFps, double? targetFps)
    {
        if (frames is not { } count || targetFps is not { } target || sourceFps is not > 0)
        {
            return frames;
        }

        return Math.Max(1, (int)Math.Round(count * target / sourceFps.Value, MidpointRounding.AwayFromZero));
    }

    private const double Tolerance = 0.001;
}
