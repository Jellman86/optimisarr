namespace Optimisarr.Core.Verification;

/// <summary>
/// Requires a software confirmation before a measured hardware-decoded VMAF failure can be used
/// to reject an output. Independent seeks into differently encoded GOP layouts can expose decoder
/// startup frames; software decoding is the authoritative fallback before a full re-encode.
/// </summary>
public static class VmafSoftwareConfirmation
{
    public static async Task<QualityResult> ConfirmAsync(
        QualityResult result,
        VerificationPolicy policy,
        Func<Task<QualityResult>> measureInSoftware) =>
        IsRequired(result, policy)
            ? await measureInSoftware()
            : result;

    public static bool IsRequired(QualityResult result, VerificationPolicy policy) =>
        result.Acceleration != VmafAcceleration.None
        && result is { Measured: true, Scores: { } scores }
        && !MeetsGate(scores, policy);

    public static bool MeetsGate(QualityScores scores, VerificationPolicy policy) =>
        scores.VmafHarmonicMean is { } harmonic
        && scores.VmafMin is { } minimum
        && harmonic >= policy.MinimumVmafHarmonicMean
        && (scores.VmafFifthPercentile ?? minimum) >= policy.MinimumVmafMin
        && minimum >= policy.MinimumVmafCatastrophicMin;
}
