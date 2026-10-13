using System.Globalization;
using System.Text.Json;

namespace Optimisarr.Core.Verification;

public readonly record struct AlignmentScore(int FrameNumber, double Score);

/// <summary>
/// Chooses one bounded frame offset using the real measurement graph. Evidence spans each
/// sampled window so a static opening cannot hide a later mismatch. Conflicting scenes keep
/// the unshifted comparison rather than concealing lost or repeated pictures (#406).
/// </summary>
public static class TimelineAlignmentProbe
{
    /// <summary>Offsets to try, in frames. Further out than one frame is a different file.</summary>
    public static IReadOnlyList<int> FramesToTry { get; } = [0, 1, -1];

    /// <summary>
    /// Length of each evidence block, and the fallback bound for commands without a window.
    /// </summary>
    public const double ProbeSeconds = 5;

    /// <summary>
    /// How much better than the unshifted timeline another offset must score to be chosen. A frame
    /// of real misalignment costs tens of points; anything closer is the scene, not the timeline.
    /// </summary>
    public const double ChoiceMarginPoints = 2;

    public const double MaximumProbeSeconds = 40;
    public const int ProbeFrameSubsample = 5;

    public static double? CommandFrameSeconds(IReadOnlyList<string> command)
    {
        var filter = command.ToList().IndexOf("-lavfi");
        if (filter < 0 || filter + 1 >= command.Count) return null;
        var match = System.Text.RegularExpressions.Regex.Match(command[filter + 1],
            @"(?:fps=fps=|round\(\{\{distortedShift\}\}\*)([0-9.eE+-]+)");
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var rate) ? FrameSeconds(rate) : null;
    }

    public static double Duration(IReadOnlyList<string> command)
    {
        var limit = command.ToList().LastIndexOf("-t");
        return limit >= 0 && limit + 1 < command.Count
            && double.TryParse(command[limit + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds) && seconds > 0
                ? Math.Min(seconds, MaximumProbeSeconds) : ProbeSeconds;
    }

    // Preserve the real seek and graph. Sparse scoring bounds metric cost while looking beyond
    // a static opening; the final measurement retains the operator's original sampling policy.
    public static IReadOnlyList<string> Prepare(IReadOnlyList<string> command)
    {
        var arguments = Truncate(command, Duration(command)).ToList();
        var filter = arguments.IndexOf("-lavfi");
        if (filter >= 0 && filter + 1 < arguments.Count)
        {
            arguments[filter + 1] = System.Text.RegularExpressions.Regex.Replace(arguments[filter + 1],
                @"n_subsample=(\d+)", match => int.TryParse(match.Groups[1].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var subsample)
                        ? "n_subsample=" + Math.Max(ProbeFrameSubsample, subsample).ToString(CultureInfo.InvariantCulture)
                        : match.Value);
        }
        return arguments;
    }

    public static IReadOnlyList<AlignmentScore>? ReadScores(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("frames", out var frames)
                || frames.ValueKind != JsonValueKind.Array) return null;
            var scores = new List<AlignmentScore>();
            foreach (var frame in frames.EnumerateArray())
            {
                if (!frame.TryGetProperty("frameNum", out var number) || !number.TryGetInt32(out var index)
                    || index < 0 || scores.Count > 0 && index <= scores[^1].FrameNumber
                    || !frame.TryGetProperty("metrics", out var metrics)
                    || !metrics.TryGetProperty("vmaf", out var metric) || !metric.TryGetDouble(out var score)
                    || !double.IsFinite(score)) return null;
                scores.Add(new(index, score));
            }
            return scores.Count > 0 ? scores : null;
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    // One offset must fit the entire window. Opposing scene-level preferences indicate drift,
    // lost pictures or ambiguity; selecting a separate winner for each scene would hide damage.
    public static int? ChooseWindow(
        IReadOnlyList<(int Frames, IReadOnlyList<AlignmentScore> Scores)> scored,
        double frameRate, double durationSeconds)
    {
        var baseline = scored.FirstOrDefault(entry => entry.Frames == 0).Scores;
        if (baseline is null || baseline.Count == 0) return null;
        if (!double.IsFinite(frameRate) || frameRate <= 0 || !double.IsFinite(durationSeconds)
            || durationSeconds <= 0 || durationSeconds > MaximumProbeSeconds) return 0;
        if (scored.Count != FramesToTry.Count || scored.Select(entry => entry.Frames).Distinct().Count() != FramesToTry.Count
            || FramesToTry.Any(offset => !scored.Any(entry => entry.Frames == offset))) return 0;
        var indices = baseline.Select(score => score.FrameNumber).ToArray();
        if (indices[0] != 0 || indices.Zip(indices.Skip(1)).Any(pair => pair.Second <= pair.First)
            || scored.Any(entry => !entry.Scores.Select(score => score.FrameNumber).SequenceEqual(indices)
                || entry.Scores.Any(score => !double.IsFinite(score.Score)))) return 0;
        var stride = indices.Length > 1 ? indices.Zip(indices.Skip(1)).Max(pair => pair.Second - pair.First) : 1;
        if (stride > 10 || indices[^1] < durationSeconds * frameRate - 2 * stride - 2
            || indices[^1] > durationSeconds * frameRate + 2 * stride + 2) return 0;
        var blocks = scored.ToDictionary(entry => entry.Frames, entry => entry.Scores
            .GroupBy(score => (int)(score.FrameNumber / (ProbeSeconds * frameRate)))
            .ToDictionary(group => group.Key, group => group.Average(score => score.Score)));
        var supported = FramesToTry.Where(offset => offset != 0).Where(offset =>
        {
            var means = blocks[offset];
            var others = blocks.Where(entry => entry.Key != offset).Select(entry => entry.Value).ToArray();
            return means.Any(block => others.All(other => block.Value - other[block.Key] >= ChoiceMarginPoints))
                && means.All(block => others.All(other => other[block.Key] - block.Value < ChoiceMarginPoints));
        }).ToArray();
        return supported.Length == 1 ? supported[0] : 0;
    }

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

    /// <summary>The chosen offset written the way the command builder writes seconds.</summary>
    public static string Format(double shiftSeconds) =>
        Math.Abs(shiftSeconds) < 0.0000005
            ? "0"
            : shiftSeconds.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>How long one picture lasts at this rate, or null when the rate is unusable.</summary>
    public static double? FrameSeconds(double? framesPerSecond) =>
        framesPerSecond is > 0 and <= 1_000 && double.IsFinite(1 / framesPerSecond.Value)
            ? 1 / framesPerSecond.Value : null;

}
