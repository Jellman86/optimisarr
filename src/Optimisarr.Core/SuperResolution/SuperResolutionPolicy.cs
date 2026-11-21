using Optimisarr.Core.Verification;

namespace Optimisarr.Core.SuperResolution;

/// <summary>
/// The verification an upscaled candidate is judged by. The size-saving gates are off, because
/// upscaling grows the file by construction. The library chooses how VMAF applies (see
/// <see cref="SuperResolutionQualityCheck"/>); every structural gate (decode, pictures, duration,
/// streams, A/V sync, colour) applies unchanged.
/// </summary>
public static class SuperResolutionPolicy
{
    /// <summary>The broken-frame floor used when the library has none of its own.</summary>
    public const double BrokenFrameFloor = 40;

    public static VerificationPolicy ForUpscaledCandidate(VerificationPolicy policy, SuperResolutionQualityCheck check)
    {
        var sized = policy with { RequireSizeReduction = false, MinimumSizeSavingPercent = null, MaximumSizeSavingPercent = null };
        return check switch
        {
            SuperResolutionQualityCheck.FullGate => sized,
            SuperResolutionQualityCheck.Off => sized with { QualityGateEnabled = false, MeasureVmaf = false },
            _ => sized with
            {
                QualityGateEnabled = true,
                MeasureVmaf = true,
                MinimumVmafHarmonicMean = 0,
                MinimumVmafMin = 0,
                MinimumVmafCatastrophicMin = policy.MinimumVmafCatastrophicMin > 0 ? policy.MinimumVmafCatastrophicMin : BrokenFrameFloor,
            },
        };
    }
}
