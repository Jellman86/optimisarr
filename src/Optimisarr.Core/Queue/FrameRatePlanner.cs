using System.Globalization;

namespace Optimisarr.Core.Queue;

/// <summary>
/// Turns a library's frame-rate cap into an exact target rate for one source, or into nothing.
///
/// The only conversion that is ever clean is dropping every other frame — an integer ratio. Any
/// other ratio (30→25, 60→24) repeats or skips a frame every few and judders, which is a worse
/// picture than either rate. So the plan halves the source rate until it sits under the cap and
/// refuses anything that cannot be reached that way. The exact result is what both the encode and
/// the VMAF reference are decimated to, so the frames being judged are the frames that were kept.
/// </summary>
public static class FrameRatePlanner
{
    /// <summary>
    /// Below this the picture stops reading as motion. A halving that lands under it (45 → 22.5)
    /// has no clean answer, so the source is left alone rather than made worse.
    /// </summary>
    public const double MinimumTargetFps = 20;

    /// <summary>
    /// The rate to decimate <paramref name="sourceFps"/> to under <paramref name="capFps"/>, or
    /// <c>null</c> when the source is already at or under the cap, when either value is unknown,
    /// or when no clean halving reaches the cap without falling below cinema rate.
    /// </summary>
    public static double? Plan(double? sourceFps, int? capFps)
    {
        if (sourceFps is not { } source || capFps is not { } cap
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
        while (target > cap * (1 + Tolerance))
        {
            target /= 2;
        }

        return target >= MinimumTargetFps ? target : null;
    }

    /// <summary>
    /// The ffmpeg <c>fps</c> filter for an exact rate. The shortest round-trip form parses back to
    /// the same double the VMAF reference timeline is built from, so both decimate identically.
    /// </summary>
    public static string Filter(double targetFps) =>
        $"fps=fps={targetFps.ToString("R", CultureInfo.InvariantCulture)}";

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
