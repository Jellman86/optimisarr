namespace Optimisarr.Core.Queue;

/// <summary>Encoder settings ordered from highest fidelity toward strongest compression.</summary>
public sealed record PerceptualQualityPlan(IReadOnlyList<int> Values, int BaselineIndex, int? MinimumValue = null)
{
    public int Baseline => Values[BaselineIndex];
    internal string FallbackSetting => MinimumValue is not null ? "the higher-fidelity retry setting" : "the library setting";

    public static PerceptualQualityPlan Image(string encoder, int baseline, int? minimumValue = null)
    {
        if (baseline is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(baseline));
        if (minimumValue is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(minimumValue));
        if (minimumValue is { } floor) baseline = Math.Max(baseline, floor);
        if (encoder is not ("mjpeg" or "libwebp")) throw new ArgumentException("Unsupported image search encoder.", nameof(encoder));
        var values = Enumerable.Range(0, 101).OrderDescending();
        // JPEG's public 0..100 control maps onto only 30 quantizers. Keep the saved
        // baseline as the representative of its quantizer so it is measured exactly once.
        var distinct = encoder == "mjpeg"
            ? values.GroupBy(FfmpegCommandBuilder.JpegQuantizer).Select(g => g.Contains(baseline) ? baseline : g.First()).ToArray()
            : values.ToArray();
        if (minimumValue is { } minimum) distinct = distinct.Where(value => value >= minimum).ToArray();
        return new(distinct, Array.IndexOf(distinct, baseline), minimumValue);
    }

    public static PerceptualQualityPlan Audio(string encoder, int baseline, int? minimumValue = null)
    {
        int[] ladder = encoder switch
        {
            "aac" => [320, 256, 224, 192, 160, 128, 112, 96, 80, 64, 48],
            "libopus" => [256, 224, 192, 160, 128, 112, 96, 80, 64, 48],
            "libmp3lame" => [320, 256, 224, 192, 160, 128, 112, 96, 80, 64],
            _ => throw new ArgumentException("Unsupported audio search encoder.", nameof(encoder))
        };
        if (baseline < ladder[^1] || baseline > ladder[0]) throw new ArgumentOutOfRangeException(nameof(baseline));
        // MP3 has discrete MPEG bitrates. A custom unsupported value would be rounded
        // by the encoder, giving two apparently different probes the same setting.
        if (encoder == "libmp3lame" && !ladder.Contains(baseline))
            throw new ArgumentException("Adaptive MP3 requires a supported discrete bitrate.", nameof(baseline));
        if (minimumValue is { } floor)
        {
            if (floor < ladder[^1] || floor > ladder[0]) throw new ArgumentOutOfRangeException(nameof(minimumValue));
            if (encoder == "libmp3lame" && !ladder.Contains(floor))
                throw new ArgumentException("Adaptive MP3 requires a supported discrete retry floor.", nameof(minimumValue));
            baseline = Math.Max(baseline, floor);
        }
        var values = ladder.Append(baseline).Concat(minimumValue is { } minimum ? new[] { minimum } : [])
            .Distinct().Where(value => minimumValue is null || value >= minimumValue).OrderDescending().ToArray();
        return new(values, Array.IndexOf(values, baseline), minimumValue);
    }
}

public sealed record PerceptualQualityProbe(int Value, bool MeetsTarget, long EncodedBytes);

public sealed record PerceptualQualityDecision(int? NextValue, int SelectedValue, bool FellBack, string Reason)
{
    public bool Complete => NextValue is null;
}

/// <summary>
/// A bounded encoder-specific search. Measurements guide compression but never authorise
/// replacement; the finished candidate must independently pass every configured gate.
/// </summary>
public static class PerceptualQualitySearch
{
    public const int MaximumProbes = 4;

    internal static PerceptualQualityDecision Unavailable(PerceptualQualityPlan plan, string? reason) =>
        new(null, plan.Baseline, true, $"Quality measurement was unavailable: {reason ?? "incomplete evidence"}. Use {plan.FallbackSetting} and final verification.");

    public static PerceptualQualityDecision Decide(PerceptualQualityPlan plan, IReadOnlyList<PerceptualQualityProbe> probes)
    {
        if (plan.Values.Count is < 1 or > 101 || plan.BaselineIndex < 0 || plan.BaselineIndex >= plan.Values.Count
            || plan.Values.Zip(plan.Values.Skip(1)).Any(pair => pair.First <= pair.Second))
            throw new ArgumentException("Search settings must be distinct and ordered from highest quality to strongest compression.", nameof(plan));
        if (probes.Any(p => p.EncodedBytes <= 0 || !plan.Values.Contains(p.Value)))
            throw new ArgumentException("Every probe must name a planned setting and positive encoded bytes.", nameof(probes));
        var measured = probes.GroupBy(p => p.Value).Select(g => g.Last())
            .OrderByDescending(p => p.Value).ToArray();
        var sawFailure = false;
        foreach (var probe in measured)
        {
            if (!probe.MeetsTarget) sawFailure = true;
            else if (sawFailure) return Fallback($"Measurements were non-monotonic; use {plan.FallbackSetting} and final verification.");
        }
        if (measured.Length >= MaximumProbes) return Select("The bounded four-candidate search completed.");
        var baseline = measured.FirstOrDefault(p => p.Value == plan.Baseline);
        if (baseline is null) return Next(plan.BaselineIndex, $"Measure {plan.FallbackSetting} first.");
        if (!baseline.MeetsTarget && !measured.Any(p => p.Value == plan.Values[0]))
            return Next(0, "Measure the highest supported fidelity to find a passing anchor.");
        var passing = measured.Where(p => p.MeetsTarget).ToArray();
        if (passing.Length == 0) return Fallback($"No measured setting met the target; use {plan.FallbackSetting} and final verification.");
        var edge = passing.MinBy(p => p.Value)!;
        var edgeIndex = Index(edge.Value);
        var failedBelow = measured.Where(p => !p.MeetsTarget && p.Value < edge.Value).MaxBy(p => p.Value);
        if (failedBelow is not null)
        {
            var failedIndex = Index(failedBelow.Value);
            return failedIndex - edgeIndex > 1
                ? Next(edgeIndex + (failedIndex - edgeIndex) / 2, "Narrow the passing and failing bracket.")
                : Select("Passing and failing settings are adjacent.");
        }
        var higher = passing.Where(p => p.Value > edge.Value).ToArray();
        if (higher.Length > 0 && edge.EncodedBytes >= higher.Min(p => p.EncodedBytes))
            return Select("More compression did not reduce measured bytes.");
        if (edgeIndex == plan.Values.Count - 1) return Select("The most compressed supported setting met the target.");
        var reach = Math.Max(Math.Max(1, plan.Values.Count / 8), edgeIndex - plan.BaselineIndex);
        return Next(Math.Min(plan.Values.Count - 1, edgeIndex + reach), "Try stronger compression while the target is met.");

        int Index(int value) => plan.Values.ToList().IndexOf(value);
        PerceptualQualityDecision Next(int index, string reason) => new(plan.Values[index], plan.Baseline, false, reason);
        PerceptualQualityDecision Fallback(string reason) => new(null, plan.Baseline, true, reason);
        PerceptualQualityDecision Select(string reason)
        {
            var best = measured.Where(p => p.MeetsTarget).OrderBy(p => p.EncodedBytes).ThenBy(p => p.Value).FirstOrDefault();
            return best is null ? Fallback($"No measured setting met the target; use {plan.FallbackSetting} and final verification.")
                : new(null, best.Value, false, reason);
        }
    }
}
