using System.Globalization;

namespace Optimisarr.Core.Queue;

/// <summary>
/// A deliberately conservative prediction made from the video-only clips already encoded for
/// adaptive quality selection. The clips are not a proof of final file size: scenes outside the
/// windows, audio, subtitles, and mux overhead can all change it. A strong predicted miss may
/// pause a job for review, but only the existing final size gate decides whether an output is safe.
/// </summary>
public static class SizePreflight
{
    private const long MinimumSourceBytes = 256L * 1024 * 1024;
    private const double HoldMargin = 1.25;

    public static SizePreflightAssessment Assess(
        long sourceBytes,
        double sourceDurationSeconds,
        double sampledDurationSeconds,
        long encodedSampleBytes,
        bool selectedProbePassedQuality,
        bool requireSizeReduction,
        double? minimumSavingPercent,
        bool bypass,
        int sampleWindowCount = 3,
        long? frozenMaximumCandidateBytes = null)
    {
        var maximum = frozenMaximumCandidateBytes ?? SizeBudget.MaxCandidateBytes(
            sourceBytes, requireSizeReduction, disposable: false, minimumSavingPercent);
        if (bypass || !selectedProbePassedQuality || maximum is null
            || sourceBytes < MinimumSourceBytes || encodedSampleBytes <= 0
            || !double.IsFinite(sourceDurationSeconds) || !double.IsFinite(sampledDurationSeconds)
            || sampledDurationSeconds <= 0 || sourceDurationSeconds <= sampledDurationSeconds * 2
            || sampleWindowCount < 3)
        {
            return SizePreflightAssessment.Inconclusive;
        }

        var projected = (double)encodedSampleBytes / sampledDurationSeconds * sourceDurationSeconds;
        if (!double.IsFinite(projected) || projected <= 0)
        {
            return SizePreflightAssessment.Inconclusive;
        }

        var projectedBytes = projected >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Ceiling(projected);
        var percentChange = (projected / sourceBytes - 1) * 100;
        var shouldHold = projected > maximum.Value * HoldMargin;
        var reason = shouldHold
            ? "Three video-only quality samples project video data at about "
              + (projected / sourceBytes * 100).ToString("0.#", CultureInfo.InvariantCulture)
              + "% of the source file, above the allowed full-output size. This estimate is uncertain; "
              + "the full encode is held until you choose to run it. The original has not changed, "
              + "and final size and quality checks still apply."
            : null;
        return new SizePreflightAssessment(shouldHold, projectedBytes, percentChange, reason);
    }
}

public sealed record SizePreflightAssessment(
    bool ShouldHold,
    long? ProjectedVideoBytes,
    double? ProjectedPercentChange,
    string? Reason)
{
    public static SizePreflightAssessment Inconclusive { get; } = new(false, null, null, null);
}
