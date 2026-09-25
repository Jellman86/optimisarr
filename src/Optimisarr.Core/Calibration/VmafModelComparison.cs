namespace Optimisarr.Core.Calibration;

/// <summary>One measured window scored by two VMAF models.</summary>
public sealed record PairedScore(double Baseline, double Candidate);

/// <summary>
/// Where a gate set on the baseline model lands on the candidate model.
/// <see cref="LinearEquivalent"/> maps it through the fitted line; <see cref="RankEquivalent"/>
/// keeps the same share of windows passing. <see cref="Agreements"/> counts windows given the same
/// verdict by the original gate on the baseline and the linear equivalent on the candidate.
/// </summary>
public sealed record ThresholdMapping(
    double BaselineThreshold,
    double LinearEquivalent,
    double RankEquivalent,
    int Windows,
    int Agreements);

public sealed record ModelComparison(
    int Count,
    double Slope,
    double Intercept,
    double Correlation,
    double MeanDifference,
    IReadOnlyList<ThresholdMapping> Thresholds);

/// <summary>
/// Compares two VMAF models on the same windows so a gate tuned on one can be stated on the other.
///
/// <para>Optimisarr's gates — harmonic mean, fifth percentile and the catastrophic floor — and every
/// calibration were set on vmaf_v0.6.1. A different model scores the same pictures on its own
/// scale, so switching models without restating the gates would silently make them stricter or
/// more lenient. This is the arithmetic the model study harness reports; the data has to come from
/// real encodes across the quality range.</para>
/// </summary>
public static class VmafModelComparison
{
    public static ModelComparison? Compare(IReadOnlyList<PairedScore> pairs, IReadOnlyList<double> baselineThresholds)
    {
        var usable = pairs
            .Where(pair => double.IsFinite(pair.Baseline) && double.IsFinite(pair.Candidate))
            .ToList();
        if (usable.Count < 3)
        {
            return null;
        }

        var meanX = usable.Average(pair => pair.Baseline);
        var meanY = usable.Average(pair => pair.Candidate);
        var covariance = usable.Sum(pair => (pair.Baseline - meanX) * (pair.Candidate - meanY));
        var varianceX = usable.Sum(pair => Math.Pow(pair.Baseline - meanX, 2));
        var varianceY = usable.Sum(pair => Math.Pow(pair.Candidate - meanY, 2));
        if (varianceX <= 0)
        {
            return null;
        }

        var slope = covariance / varianceX;
        var intercept = meanY - slope * meanX;
        var correlation = varianceY > 0 ? covariance / Math.Sqrt(varianceX * varianceY) : 0;

        var candidates = usable.Select(pair => pair.Candidate).Order().ToList();
        var mappings = baselineThresholds.Select(threshold =>
        {
            var linear = slope * threshold + intercept;
            var passing = usable.Count(pair => pair.Baseline >= threshold);
            var agreements = usable.Count(pair => pair.Baseline >= threshold == pair.Candidate >= linear);
            return new ThresholdMapping(threshold, linear, SamePassingCount(candidates, passing), usable.Count, agreements);
        }).ToList();

        return new ModelComparison(usable.Count, slope, intercept, correlation, meanY - meanX, mappings);
    }

    /// <summary>
    /// The lowest candidate score among the top <paramref name="passing"/>, so the same number of
    /// windows clears it; <paramref name="sorted"/> is ascending.
    /// </summary>
    private static double SamePassingCount(IReadOnlyList<double> sorted, int passing) =>
        passing <= 0 ? sorted[^1] : sorted[Math.Max(0, sorted.Count - passing)];
}
