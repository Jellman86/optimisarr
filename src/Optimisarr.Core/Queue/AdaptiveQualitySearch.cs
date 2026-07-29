namespace Optimisarr.Core.Queue;

/// <summary>A measured candidate from the bounded adaptive-quality search.</summary>
public sealed record AdaptiveQualityProbe(int Quality, bool MeetsTarget, long EncodedBytes);

/// <summary>The next candidate to measure, or the final quality selected by the search.</summary>
public sealed record AdaptiveQualityDecision(int? NextQuality, int SelectedQuality, bool FellBack, string Reason)
{
    public bool Complete => NextQuality is null;
}

/// <summary>
/// Plans a small, deterministic search around the library's encoder-specific quality value.
/// Larger CRF/CQ/ICQ/QP values guide the search toward a likely smaller result, but the final
/// selection uses the actual encoded sample bytes rather than assuming the encoder's size ordering.
/// The search never exceeds four candidates, never leaves the codec's common 0..51 range, and falls
/// back to the library value if VMAF measurements contradict the expected quality ordering.
/// </summary>
public static class AdaptiveQualitySearch
{
    public const int MaximumProbes = 4;
    public const int MaximumOffset = 6;

    public static AdaptiveQualityDecision Decide(
        int baselineQuality,
        IReadOnlyList<AdaptiveQualityProbe> probes)
    {
        var baseline = Math.Clamp(baselineQuality, 0, 51);
        var distinct = probes
            .GroupBy(probe => Math.Clamp(probe.Quality, 0, 51))
            .Select(group => group.Last() with { Quality = group.Key })
            .OrderBy(probe => probe.Quality)
            .ToList();

        if (HasNonMonotonicEvidence(distinct))
        {
            return Complete(baseline, fellBack: true,
                "Sample scores were non-monotonic; using the library quality.");
        }

        if (distinct.Count >= MaximumProbes)
        {
            return SelectMeasured(baseline, distinct, "The bounded four-candidate search completed.");
        }

        var baselineProbe = distinct.FirstOrDefault(probe => probe.Quality == baseline);
        if (baselineProbe is null)
        {
            return Next(baseline, baseline, "Measure the library quality first.");
        }

        var lower = Math.Max(0, baseline - MaximumOffset);
        var upper = Math.Min(51, baseline + MaximumOffset);
        if (baselineProbe.MeetsTarget)
        {
            if (!Contains(distinct, upper))
            {
                return Next(upper, baseline, "Bracket the most space-efficient passing quality.");
            }
        }
        else if (!Contains(distinct, lower))
        {
            return Next(lower, baseline, "Bracket a higher-quality value that clears the target.");
        }

        var passes = distinct.Where(probe => probe.MeetsTarget).Select(probe => probe.Quality).ToList();
        if (passes.Count == 0)
        {
            return Complete(baseline, fellBack: true,
                "No sampled quality cleared the target; using the library quality and final verification.");
        }

        var highestPass = passes.Max();
        var firstFailAbove = distinct
            .Where(probe => !probe.MeetsTarget && probe.Quality > highestPass)
            .Select(probe => (int?)probe.Quality)
            .Min();

        if (firstFailAbove is null)
        {
            return Complete(MostSpaceEfficientPass(distinct), fellBack: false,
                "The smallest measured passing candidate cleared every sample.");
        }

        if (firstFailAbove.Value - highestPass <= 1)
        {
            return Complete(MostSpaceEfficientPass(distinct), fellBack: false,
                "The passing and failing qualities were resolved to adjacent values.");
        }

        var midpoint = highestPass + ((firstFailAbove.Value - highestPass) / 2);
        if (Contains(distinct, midpoint))
        {
            return SelectMeasured(baseline, distinct, "No unmeasured value remains in the bracket.");
        }

        return Next(midpoint, highestPass, "Narrow the passing/failing quality bracket.");
    }

    private static bool HasNonMonotonicEvidence(IReadOnlyList<AdaptiveQualityProbe> probes)
    {
        var sawFailure = false;
        foreach (var probe in probes)
        {
            if (!probe.MeetsTarget)
            {
                sawFailure = true;
            }
            else if (sawFailure)
            {
                return true;
            }
        }
        return false;
    }

    private static bool Contains(IEnumerable<AdaptiveQualityProbe> probes, int quality) =>
        probes.Any(probe => probe.Quality == quality);

    private static AdaptiveQualityDecision SelectMeasured(
        int baseline,
        IReadOnlyList<AdaptiveQualityProbe> probes,
        string reason)
    {
        var passing = probes.Where(probe => probe.MeetsTarget).ToList();
        return passing.Count > 0
            ? Complete(MostSpaceEfficientPass(passing), fellBack: false, reason)
            : Complete(baseline, fellBack: true,
                "No sampled quality cleared the target; using the library quality and final verification.");
    }

    private static int MostSpaceEfficientPass(IEnumerable<AdaptiveQualityProbe> probes) =>
        probes
            .Where(probe => probe.MeetsTarget)
            .OrderBy(probe => probe.EncodedBytes)
            // A deterministic tie prefers the larger quality value, which is the usual
            // constant-quality direction and gives the next full encode the same search bias.
            .ThenByDescending(probe => probe.Quality)
            .First()
            .Quality;

    private static AdaptiveQualityDecision Next(int next, int selected, string reason) =>
        new(next, selected, FellBack: false, reason);

    private static AdaptiveQualityDecision Complete(int selected, bool fellBack, string reason) =>
        new(null, selected, fellBack, reason);
}
