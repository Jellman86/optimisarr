namespace Optimisarr.Core.Calibration;

public sealed record CalibrationScore(string Source, double Baseline, double Candidate);

public sealed record GateCalibration(double ProposedThreshold, int HeldOutSources, int UnsafeAccepts,
    int BaselinePasses, int RejectedBaselinePasses)
{
    // Agreement with the established policy is a necessary regression check, not proof of visual
    // preference or corpus coverage. Rejecting everything is not a useful calibration.
    public bool Passed => UnsafeAccepts == 0 && BaselinePasses > 0
        && (double)RejectedBaselinePasses / BaselinePasses <= 0.2;
}

public static class VmafGateCalibration
{
    /// <summary>
    /// Fits a conservative threshold without the source being evaluated. Windows and quality
    /// variants of the same source always stay together, preventing training/validation leakage.
    /// A fit requires both passing and failing examples for that gate in every training fold.
    /// </summary>
    public static GateCalibration? Validate(IReadOnlyList<CalibrationScore> scores, double baselineThreshold)
    {
        if (!double.IsFinite(baselineThreshold) || scores.Any(s => string.IsNullOrWhiteSpace(s.Source)
                || !double.IsFinite(s.Baseline) || !double.IsFinite(s.Candidate))) return null;
        var groups = scores.GroupBy(s => s.Source).ToList();
        if (groups.Count < 3) return null;
        var unsafeAccepts = 0;
        var baselinePasses = 0;
        var rejected = 0;
        foreach (var group in groups)
        {
            var training = scores.Where(s => s.Source != group.Key).ToList();
            if (Fit(training, baselineThreshold) is not { } threshold) return null;
            foreach (var row in group)
            {
                var passedBefore = row.Baseline >= baselineThreshold;
                var passedAfter = row.Candidate >= threshold;
                if (!passedBefore && passedAfter) unsafeAccepts++;
                if (passedBefore) baselinePasses++;
                if (passedBefore && !passedAfter) rejected++;
            }
        }
        return new(Fit(scores, baselineThreshold)!.Value, groups.Count, unsafeAccepts, baselinePasses, rejected);
    }

    private static double? Fit(IReadOnlyList<CalibrationScore> scores, double baselineThreshold)
    {
        var failures = scores.Where(s => s.Baseline < baselineThreshold).ToList();
        if (failures.Count == 0 || failures.Count == scores.Count) return null;
        var highestFailure = failures.Max(s => s.Candidate);
        var lowestPass = scores.Where(s => s.Baseline >= baselineThreshold).Min(s => s.Candidate);
        // Centre a separating gap so normal variation in a held-out source does not immediately
        // cross a threshold placed just 0.01 above the training maximum. If classes overlap,
        // protect the observed failures; validation will expose the resulting rejected passes.
        return lowestPass > highestFailure
            ? Math.Ceiling((highestFailure + lowestPass) * 50) / 100
            : (Math.Floor(highestFailure * 100) + 1) / 100;
    }
}
