namespace Optimisarr.Core.Verification;

/// <summary>Research observations only. No field in this record is a replacement gate.</summary>
public sealed record VmafShadowEvidence(
    string Status,
    string? Detail,
    IReadOnlyList<VmafShadowWindow> Windows,
    string BaselineModel,
    string CandidateModel,
    DateTimeOffset RecordedAt,
    QualityMeasurementContext? Context,
    string MeasurementLocation = "Server",
    string Policy = "sdr-native-reference-cpu-paired-10s-v1");

public sealed record VmafShadowWindow(
    int StartSeconds,
    int DurationSeconds,
    string? DistortedShift,
    QualityScores? Baseline,
    QualityScores? Candidate);

public static class VmafShadowPlan
{
    public const string HdModel = "vmaf_v1.0.16_3d0h";
    public const string UhdModel = "vmaf_v1.0.16_1d5h_2160";

    // Invalid research numbers must never make the surrounding verification report unserialisable.
    public static QualityMeasurementContext? RetainContext(QualityMeasurementContext context) =>
        new[] { context.ReferenceDurationSeconds, context.ReferenceFrameRate,
            context.ReferenceContainerLeadSeconds, context.DistortedContainerLeadSeconds,
            context.ReferenceDecimation?.SourceFps, context.ReferenceDecimation?.TargetFps }
            .All(value => value is null || double.IsFinite(value.Value)) ? context : null;

    public static QualityScores? RetainScores(QualityScores? scores) => scores is not null
        && new[] { scores.VmafMean, scores.VmafHarmonicMean, scores.VmafMin, scores.VmafFifthPercentile,
            scores.PsnrYMean, scores.SsimMean }.All(value => value is null || double.IsFinite(value.Value))
        ? scores : null;

    public static string? SkipReason(QualityMeasurementContext context)
    {
        if (context.ReferenceIsHdr || context.HdrConvertedToSdr)
            return "HDR and tone-mapped sources are outside this SDR study.";
        if (context.ReferenceFrameRate is not > 0 or >= 45)
            return "Research requires a known source frame rate below 45 fps.";
        if (context.ReferenceDecimation is not null)
            return "Frame-rate conversion is outside this initial study.";
        if (context.ReferenceWidth <= 0 || context.ReferenceHeight <= 0
            || context.EncodedVideo is not { Width: > 0, Height: > 0, BitDepth: 8 or 10 })
            return "Research requires known source dimensions and an 8/10-bit candidate format.";
        if (context.ReferenceDurationSeconds is not { } duration || !double.IsFinite(duration)
            || duration < 1 || duration > int.MaxValue)
            return "Research requires a finite source duration of at least one second.";
        return null;
    }

    public static IReadOnlyList<VmafWindow> Windows(double durationSeconds)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds < 1 || durationSeconds > int.MaxValue) return [];
        var seconds = Math.Min(10, (int)Math.Floor(durationSeconds));
        var last = (int)Math.Floor(durationSeconds) - seconds;
        return new[] { 0.1, 0.5, 0.9 }
            .Select(fraction => Math.Clamp((int)Math.Floor(durationSeconds * fraction) - seconds / 2, 0, last))
            .Distinct().Select(start => new VmafWindow(start, seconds)).ToList();
    }

    public static bool ValidPair(QualityScores? baseline, QualityScores? candidate) =>
        ValidScores(baseline) && ValidScores(candidate) && baseline!.FrameCount == candidate!.FrameCount;

    private static bool ValidScores(QualityScores? scores) => scores is { FrameCount: > 0 }
        && new[] { scores.VmafMean, scores.VmafHarmonicMean, scores.VmafMin, scores.VmafFifthPercentile }
            .All(value => value is { } number && double.IsFinite(number));
}
