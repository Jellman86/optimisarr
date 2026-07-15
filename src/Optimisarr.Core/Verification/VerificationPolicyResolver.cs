namespace Optimisarr.Core.Verification;

/// <summary>
/// Pure resolution of the effective verification policy for a single job. The global
/// policy from settings is the baseline; a library may override the VMAF quality-gate
/// state, thresholds, and sampling so different content can demand different quality
/// (an archive library near-lossless, a space-saver more forgiving). Null inherits the
/// corresponding global value; numeric overrides are clamped to their valid ranges.
/// </summary>
public sealed record VerificationPolicyOverrides(
    bool? QualityGateEnabled,
    double? MinimumVmafHarmonicMean,
    double? MinimumVmafFifthPercentile,
    double? MinimumVmafCatastrophicMin,
    bool? ClipVmafEnabled,
    int? VmafFrameSubsample);

public static class VerificationPolicyResolver
{
    public static VerificationPolicy Resolve(
        VerificationPolicy global,
        VerificationPolicyOverrides overrides)
    {
        return global with
        {
            QualityGateEnabled = overrides.QualityGateEnabled ?? global.QualityGateEnabled,
            MinimumVmafHarmonicMean = Clamp(overrides.MinimumVmafHarmonicMean) ?? global.MinimumVmafHarmonicMean,
            MinimumVmafMin = Clamp(overrides.MinimumVmafFifthPercentile) ?? global.MinimumVmafMin,
            MinimumVmafCatastrophicMin = Clamp(overrides.MinimumVmafCatastrophicMin) ?? global.MinimumVmafCatastrophicMin,
            ClipVmafEnabled = overrides.ClipVmafEnabled ?? global.ClipVmafEnabled,
            VmafFrameSubsample = overrides.VmafFrameSubsample is { } interval
                ? Math.Clamp(interval, 1, QualityScoreCommandBuilder.MaximumFrameSubsample)
                : global.VmafFrameSubsample
        };
    }

    private static double? Clamp(double? value) =>
        value is null ? null : Math.Clamp(value.Value, 0, 100);
}
