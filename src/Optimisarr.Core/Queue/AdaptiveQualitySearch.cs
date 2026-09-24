using Optimisarr.Core.Verification;

namespace Optimisarr.Core.Queue;

/// <summary>
/// A measured candidate from the bounded adaptive-quality search.
///
/// <para><see cref="Scores"/> is what the verdict was reached on, kept rather than discarded. The
/// search used to record only whether a candidate met the gate, so a run that rejected every
/// candidate and fell back to the library's quality left nothing behind saying how far off they
/// were — and the first search ever watched end to end did exactly that, on a file whose finished
/// encode at the same quality then scored 91.5. Optional because probes recorded before this
/// existed are still read back from the lease.</para>
///
/// <para><see cref="WindowEncodedBytes"/> splits <see cref="EncodedBytes"/> by sample window so the
/// size forecast can say which scenes grew. Optional because an older worker reports only the
/// total, and the forecast itself needs nothing more.</para>
/// </summary>
public sealed record AdaptiveQualityProbe(
    int Quality,
    bool MeetsTarget,
    long EncodedBytes,
    QualityScores? Scores = null,
    IReadOnlyList<long>? WindowEncodedBytes = null);

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

    /// <summary>
    /// How far from the library's quality the search first looks, in either direction.
    ///
    /// <para>A starting width, not a ceiling. It used to be both, and the difference was the whole
    /// value of the feature: when the far end of the bracket <em>passed</em>, the search concluded
    /// there rather than going further, so every job settled at exactly baseline + 6. Measured
    /// across seven episodes on an RTX 4070, that end was clearing a gate of 85 with a harmonic
    /// mean of 96 and less than half the bytes — eleven points of headroom, unclaimed, every time.
    /// The bound was deciding the answer.</para>
    /// </summary>
    public const int InitialOffset = 6;

    /// <summary>
    /// Kept for anything still reading the old name. The bracket no longer stops here.
    /// </summary>
    public const int MaximumOffset = InitialOffset;

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

        var lower = Math.Max(0, baseline - InitialOffset);
        if (baselineProbe.MeetsTarget)
        {
            // Step outward while the far end keeps passing, doubling the reach each time. A bracket
            // whose end clears the gate has not found the edge; it has only proved that everything
            // inside it is acceptable, and stopping there reports the width of the bracket rather
            // than anything about the title.
            if (NextOutward(distinct, baseline) is { } outward)
            {
                return Next(outward, baseline, "Reach further out while the target is still being cleared.");
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

    /// <summary>
    /// The next quality to try beyond everything measured so far, or nothing when there is no
    /// further out to go.
    ///
    /// <para>The reach doubles: the first step out is <see cref="InitialOffset"/> from the
    /// library's value, the next twice that, and so on. Exponential rather than linear because the
    /// probe budget is four and a linear walk would spend all of it creeping. Doubling finds the
    /// edge of what a title tolerates in two or three samples and leaves the rest for bisecting
    /// back to it.</para>
    ///
    /// <para>Only ever called when every measured quality passed, so there is always a passing
    /// anchor to come back to: a step out that fails costs one sample and gives the bisection its
    /// upper bound.</para>
    /// </summary>
    private static int? NextOutward(IReadOnlyList<AdaptiveQualityProbe> probes, int baseline)
    {
        var furthestProbe = probes.MaxBy(probe => probe.Quality)!;
        var furthest = furthestProbe.Quality;

        // Only from a passing anchor. The caller reaches here whenever the *baseline* passed, which
        // is not the same as everything having passed: with a failure already out there the bracket
        // is found and the next move is to bisect back into it, not to step past it. Stepping out
        // from a failure asked for 51 where the answer was 32.
        if (!furthestProbe.MeetsTarget)
        {
            return null;
        }

        if (furthest < baseline + InitialOffset)
        {
            var first = Math.Min(51, baseline + InitialOffset);
            return first > furthest ? first : null;
        }

        // Only while going further out is still winning. A larger quality value usually means a
        // smaller file, but only usually — this whole search exists because the encoder's own size
        // ordering cannot be assumed. When the step out passed and came back no smaller, the title
        // is not responding to the number in the expected direction and another step is a sample
        // encode spent to learn nothing.
        var smallestBelow = probes
            .Where(probe => probe.MeetsTarget && probe.Quality < furthest)
            .Select(probe => (long?)probe.EncodedBytes)
            .Min();
        if (smallestBelow is { } best && furthestProbe.EncodedBytes >= best)
        {
            return null;
        }

        // How far the last step reached, doubled. Measured from the baseline so the sequence is
        // 6, 18, 42… rather than drifting with wherever a bisection happened to land.
        var next = Math.Min(51, baseline + ((furthest - baseline) * 2));
        return next > furthest ? next : null;
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
