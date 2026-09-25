using System.Globalization;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;

namespace Optimisarr.Api.Workers;

/// <summary>
/// Says what a candidate actually scored, beside the gate it was held to.
///
/// <para>"Missed the VMAF target" is a verdict without evidence. A search that rejects every
/// candidate and falls back to the library's quality reads identically whether the samples were
/// far below the gate or a whisker under it, and identically again whether the measurement itself
/// was sound — which matters, because a finished encode of the same file at the same quality has
/// been seen to score well above a gate its own samples missed. The numbers are written once,
/// beside the verdict, wherever a search runs.</para>
/// </summary>
internal static class AdaptiveProbeReport
{
    public static string Describe(AdaptiveQualityProbe probe, VerificationPolicy policy)
    {
        if (probe.Scores is not { } scores)
        {
            return "no scores recorded";
        }

        // Each figure beside the threshold that actually judges it. They are not the ones you
        // would guess: the *fifth percentile* is held to MinimumVmafMin, and the lowest frame is
        // held to the catastrophic floor, which is far lower. Pairing the lowest frame with
        // MinimumVmafMin — as this line did when it was written — printed
        // "lowest 62.07 of 70 ... met the VMAF target" on a candidate that had passed perfectly
        // well, and left the number that was actually deciding with no threshold beside it at all.
        // See VmafSoftwareConfirmation.MeetsGate.
        return string.Create(
            CultureInfo.InvariantCulture,
            $"harmonic {Number(scores.VmafHarmonicMean)} of {Number(policy.MinimumVmafHarmonicMean)}, "
            + $"fifth percentile {Number(scores.VmafFifthPercentile ?? scores.VmafMin)} of {Number(policy.MinimumVmafMin)}, "
            + $"lowest {Number(scores.VmafMin)} of {Number(policy.MinimumVmafCatastrophicMin)}, "
            + $"mean {Number(scores.VmafMean)}, {Number(scores.FrameCount)} frames");
    }

    private static string Number(double? value) =>
        value is { } number ? number.ToString("0.##", CultureInfo.InvariantCulture) : "unmeasured";

    private static string Number(int? value) =>
        value is { } number ? number.ToString(CultureInfo.InvariantCulture) : "unmeasured";
}
