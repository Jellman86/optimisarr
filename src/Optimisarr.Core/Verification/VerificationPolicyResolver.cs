namespace Optimisarr.Core.Verification;

/// <summary>
/// Pure resolution of the effective verification policy for a single job. Every configurable
/// gate belongs to the library because acceptable output differs by content and intent. Null
/// values retain the supplied baseline for legacy API/config compatibility; numeric values are
/// clamped to their valid ranges.
/// </summary>
public sealed record VerificationPolicyOverrides(
    bool? QualityGateEnabled,
    double? MinimumVmafHarmonicMean,
    double? MinimumVmafFifthPercentile,
    double? MinimumVmafCatastrophicMin,
    bool? ClipVmafEnabled,
    int? VmafFrameSubsample,
    double? DurationTolerancePercent = null,
    bool? RequireAudioRetained = null,
    bool? RequireSubtitlesRetained = null,
    bool? RequireSizeReduction = null,
    bool? AudioLoudnessGateEnabled = null,
    double? MaxLoudnessDriftLufs = null,
    bool? AudioClippingGateEnabled = null,
    double? MaxTruePeakDbtp = null,
    bool? ImageQualityGateEnabled = null,
    double? MinimumImageSsim = null,
    bool? ImageMetadataGateEnabled = null);

public static class VerificationPolicyResolver
{
    public static VerificationPolicy Resolve(
        VerificationPolicy baseline,
        VerificationPolicyOverrides overrides)
    {
        return baseline with
        {
            DurationTolerancePercent = ClampMinimum(overrides.DurationTolerancePercent, 0)
                ?? baseline.DurationTolerancePercent,
            RequireAudioRetained = overrides.RequireAudioRetained ?? baseline.RequireAudioRetained,
            RequireSubtitlesRetained = overrides.RequireSubtitlesRetained ?? baseline.RequireSubtitlesRetained,
            RequireSizeReduction = overrides.RequireSizeReduction ?? baseline.RequireSizeReduction,
            QualityGateEnabled = overrides.QualityGateEnabled ?? baseline.QualityGateEnabled,
            MinimumVmafHarmonicMean = ClampRange(overrides.MinimumVmafHarmonicMean, 0, 100)
                ?? baseline.MinimumVmafHarmonicMean,
            MinimumVmafMin = ClampRange(overrides.MinimumVmafFifthPercentile, 0, 100)
                ?? baseline.MinimumVmafMin,
            MinimumVmafCatastrophicMin = ClampRange(overrides.MinimumVmafCatastrophicMin, 0, 100)
                ?? baseline.MinimumVmafCatastrophicMin,
            ClipVmafEnabled = overrides.ClipVmafEnabled ?? baseline.ClipVmafEnabled,
            VmafFrameSubsample = overrides.VmafFrameSubsample is { } interval
                ? Math.Clamp(interval, 1, QualityScoreCommandBuilder.MaximumFrameSubsample)
                : baseline.VmafFrameSubsample,
            AudioLoudnessGateEnabled = overrides.AudioLoudnessGateEnabled
                ?? baseline.AudioLoudnessGateEnabled,
            MaxLoudnessDriftLufs = ClampMinimum(overrides.MaxLoudnessDriftLufs, 0)
                ?? baseline.MaxLoudnessDriftLufs,
            AudioClippingGateEnabled = overrides.AudioClippingGateEnabled
                ?? baseline.AudioClippingGateEnabled,
            MaxTruePeakDbtp = overrides.MaxTruePeakDbtp is { } truePeak && double.IsFinite(truePeak)
                ? truePeak
                : baseline.MaxTruePeakDbtp,
            ImageQualityGateEnabled = overrides.ImageQualityGateEnabled
                ?? baseline.ImageQualityGateEnabled,
            MinimumImageSsim = ClampRange(overrides.MinimumImageSsim, 0, 1)
                ?? baseline.MinimumImageSsim,
            ImageMetadataGateEnabled = overrides.ImageMetadataGateEnabled
                ?? baseline.ImageMetadataGateEnabled
        };
    }

    private static double? ClampMinimum(double? value, double minimum) =>
        value is not { } number || !double.IsFinite(number) ? null : Math.Max(number, minimum);

    private static double? ClampRange(double? value, double minimum, double maximum) =>
        value is not { } number || !double.IsFinite(number)
            ? null
            : Math.Clamp(number, minimum, maximum);
}
