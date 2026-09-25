using System.Globalization;
using System.Text.Json;

namespace Optimisarr.Core.Verification;

/// <summary>
/// Finds how far the candidate's pictures sit from the reference's, by trying.
///
/// <para>The shift this replaces was derived from the containers' own account of themselves — the
/// distorted's lead less the reference's. That number is zero for almost every real pair, so the
/// correction was seldom applied; and where it mattered it could not have been right anyway. Two
/// episodes of the same show, encoded by the same command on the same machine, are identical in
/// every header field and need opposite answers:</para>
///
/// <code>
/// S13E20   shift 0 → harmonic  0.21      shift +1 frame → harmonic 92.32
/// S13E21   shift 0 → harmonic 89.88      shift +1 frame → harmonic  0.31
/// </code>
///
/// <para>The difference is not in the headers. It is that frames are still occasionally lost in an
/// encode — one in the first of those files, six in the second — so whether a window lines up
/// depends on how many went missing before it. No arithmetic over metadata can know that.</para>
///
/// <para>Both sidecars measure this the same way, to the same offsets. A server that guessed while
/// its workers measured would disagree with them about the same pair of files.</para>
/// </summary>
public static class TimelineAlignmentProbe
{
    /// <summary>Offsets to try, in frames. Further out than one frame is a different file.</summary>
    public static IReadOnlyList<int> FramesToTry { get; } = [0, 1, -1];

    /// <summary>
    /// How much of the window each offset is tried on. Five seconds is long enough to hold motion
    /// or a cut, which is what tells a frame of misalignment apart; two seconds of a static shot
    /// scored every offset alike and let noise choose.
    /// </summary>
    public const double ProbeSeconds = 5;

    /// <summary>
    /// How much better than the unshifted timeline another offset must score to be chosen. A frame
    /// of real misalignment costs tens of points; anything closer is the scene, not the timeline.
    /// </summary>
    public const double ChoiceMarginPoints = 2;

    /// <summary>
    /// The measurement itself, stopped after <paramref name="seconds"/> of output.
    ///
    /// <para>The probe must be the measurement's own graph. An earlier probe built its own — no
    /// cadence grid, no lead correction, a different seek — and its best offset was one frame wrong
    /// for the measurement that followed: a clean encode scoring 95 unshifted was failed at 9.</para>
    /// </summary>
    public static IReadOnlyList<string> Truncate(IReadOnlyList<string> measurement, double seconds)
    {
        var length = seconds.ToString("G", CultureInfo.InvariantCulture);
        var arguments = measurement.ToList();
        var limit = arguments.LastIndexOf("-t");
        if (limit >= 0 && limit + 1 < arguments.Count)
        {
            arguments[limit + 1] = length;
            return arguments;
        }

        var output = arguments.LastIndexOf("-f");
        arguments.InsertRange(output >= 0 ? output : arguments.Count, ["-t", length]);
        return arguments;
    }

    /// <summary>
    /// The offset, in frames, whose probe matched best, or null when none could be scored. The
    /// unshifted timeline keeps the window unless another offset beats it clearly.
    /// </summary>
    public static int? Choose(IReadOnlyList<(int Frames, double Mean)> scored)
    {
        if (scored.Count == 0)
        {
            return null;
        }

        var best = scored.MaxBy(entry => entry.Mean);
        return best.Frames != 0
            && scored.Any(entry => entry.Frames == 0 && best.Mean - entry.Mean < ChoiceMarginPoints)
                ? 0
                : best.Frames;
    }

    /// <summary>
    /// The mean of a probe's frame scores. The mean rather than the harmonic mean on purpose: this
    /// chooses between alignments rather than judging quality, and a harmonic mean collapses to
    /// near zero for every offset once any of them holds a zero.
    /// </summary>
    public static double? MeanScore(string json)
    {
        List<double> scores = [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("frames", out var frames)
                || frames.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var frame in frames.EnumerateArray())
            {
                if (frame.TryGetProperty("metrics", out var metrics)
                    && metrics.TryGetProperty("vmaf", out var vmaf)
                    && vmaf.TryGetDouble(out var score))
                {
                    scores.Add(score);
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return scores.Count == 0 ? null : scores.Average();
    }

    /// <summary>The chosen offset written the way the command builder writes seconds.</summary>
    public static string Format(double shiftSeconds) =>
        Math.Abs(shiftSeconds) < 0.0000005
            ? "0"
            : shiftSeconds.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>How long one picture lasts at this rate, or null when the rate is unusable.</summary>
    public static double? FrameSeconds(double? framesPerSecond) =>
        framesPerSecond is > 0 ? 1 / framesPerSecond.Value : null;

}
