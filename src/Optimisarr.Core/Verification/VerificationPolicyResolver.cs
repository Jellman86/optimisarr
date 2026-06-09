namespace Optimisarr.Core.Verification;

/// <summary>
/// Pure resolution of the effective verification policy for a single job. The global
/// policy from settings is the baseline; a library may override the VMAF quality-gate
/// thresholds so different content can demand different quality (an archive library
/// near-lossless, a space-saver more forgiving). Overrides only matter when the gate
/// is enabled and are clamped to the valid 0–100 VMAF range.
/// </summary>
public static class VerificationPolicyResolver
{
    public static VerificationPolicy Resolve(
        VerificationPolicy global,
        double? minVmafHarmonicMeanOverride,
        double? minVmafMinOverride)
    {
        if (!global.QualityGateEnabled
            || (minVmafHarmonicMeanOverride is null && minVmafMinOverride is null))
        {
            return global;
        }

        return global with
        {
            MinimumVmafHarmonicMean = Clamp(minVmafHarmonicMeanOverride) ?? global.MinimumVmafHarmonicMean,
            MinimumVmafMin = Clamp(minVmafMinOverride) ?? global.MinimumVmafMin
        };
    }

    private static double? Clamp(double? value) =>
        value is null ? null : Math.Clamp(value.Value, 0, 100);
}
