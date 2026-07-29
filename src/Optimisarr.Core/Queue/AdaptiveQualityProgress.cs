namespace Optimisarr.Core.Queue;

/// <summary>The two measurable phases of one adaptive-quality sample window.</summary>
public enum AdaptiveQualityPhase
{
    Encoding,
    Measuring
}

/// <summary>
/// Maps a bounded candidate/window/phase into one monotonic preparation fraction.
/// </summary>
public static class AdaptiveQualityProgress
{
    public const double Started = 0.001;

    public static double Map(
        int candidateIndex,
        int windowIndex,
        int windowCount,
        AdaptiveQualityPhase phase,
        double phaseProgress)
    {
        if (candidateIndex < 0 || windowIndex < 0 || windowCount <= 0 || windowIndex >= windowCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowIndex),
                "Candidate and window indexes must identify a planned adaptive sample.");
        }

        var phasesPerCandidate = windowCount * 2;
        var phaseIndex = candidateIndex * phasesPerCandidate
            + windowIndex * 2
            + (phase == AdaptiveQualityPhase.Measuring ? 1 : 0);
        var totalPhases = AdaptiveQualitySearch.MaximumProbes * phasesPerCandidate;
        return Math.Clamp(
            (phaseIndex + Math.Clamp(phaseProgress, 0, 1)) / totalPhases,
            Started,
            0.999);
    }
}
