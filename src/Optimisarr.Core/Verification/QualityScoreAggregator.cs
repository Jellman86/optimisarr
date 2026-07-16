namespace Optimisarr.Core.Verification;

/// <summary>Combines independently measured VMAF windows into one conservative score.</summary>
public static class QualityScoreAggregator
{
    public static QualityResult Combine(IReadOnlyList<QualityResult> results, string samplingDescription)
    {
        var failure = results.FirstOrDefault(result => !result.Measured || result.Scores is null);
        if (failure is not null)
        {
            return QualityResult.Failed(failure.Error ?? "A VMAF sample produced no usable score.");
        }

        var scores = results.Select(result => result.Scores!).ToList();
        if (scores.Count == 0)
        {
            return QualityResult.Failed("No VMAF samples were measured.");
        }

        return QualityResult.Ok(new QualityScores(
            VmafMean: WeightedAverage(scores, score => score.VmafMean),
            VmafHarmonicMean: WeightedHarmonicAverage(scores, score => score.VmafHarmonicMean),
            VmafMin: Minimum(scores, score => score.VmafMin),
            PsnrYMean: WeightedAverage(scores, score => score.PsnrYMean),
            SsimMean: WeightedAverage(scores, score => score.SsimMean),
            ModelVersion: CommonValue(scores.Select(score => score.ModelVersion)),
            Preprocessing: CombineDescription(
                CommonValue(scores.Select(score => score.Preprocessing)),
                samplingDescription),
            // We cannot reconstruct the exact pooled percentile from summary values. Taking the
            // weakest window's percentile is deterministic and deliberately conservative.
            VmafFifthPercentile: Minimum(scores, score => score.VmafFifthPercentile),
            FrameCount: scores.Sum(score => score.FrameCount ?? 0)));
    }

    private static double? WeightedAverage(
        IReadOnlyList<QualityScores> scores,
        Func<QualityScores, double?> selector)
    {
        var measured = scores
            .Select(score => (Value: selector(score), Weight: Math.Max(1, score.FrameCount ?? 1)))
            .Where(item => item.Value is not null)
            .ToList();
        return measured.Count == 0
            ? null
            : measured.Sum(item => item.Value!.Value * item.Weight) / measured.Sum(item => item.Weight);
    }

    private static double? WeightedHarmonicAverage(
        IReadOnlyList<QualityScores> scores,
        Func<QualityScores, double?> selector)
    {
        var measured = scores
            .Select(score => (Value: selector(score), Weight: Math.Max(1, score.FrameCount ?? 1)))
            .Where(item => item.Value is > 0)
            .ToList();
        return measured.Count == 0
            ? null
            : measured.Sum(item => item.Weight)
                / measured.Sum(item => item.Weight / item.Value!.Value);
    }

    private static double? Minimum(
        IEnumerable<QualityScores> scores,
        Func<QualityScores, double?> selector)
    {
        var values = scores.Select(selector).Where(value => value is not null).Select(value => value!.Value);
        return values.Any() ? values.Min() : null;
    }

    private static string? CommonValue(IEnumerable<string?> values)
    {
        var distinct = values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToList();
        return distinct.Count == 1 ? distinct[0] : null;
    }

    private static string CombineDescription(string? preprocessing, string samplingDescription) =>
        string.IsNullOrWhiteSpace(preprocessing)
            ? samplingDescription
            : $"{preprocessing}; {samplingDescription}";
}
