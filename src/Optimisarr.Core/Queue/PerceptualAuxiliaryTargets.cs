using Optimisarr.Core.Verification;

namespace Optimisarr.Core.Queue;

/// <summary>Independent configured gates guide samples without weakening the perceptual target.</summary>
public static class PerceptualAuxiliaryTargets
{
    public static PerceptualSearchMeasurement Audio(PerceptualSearchMeasurement measurement,
        LoudnessResult? source, LoudnessResult? candidate, VerificationPolicy policy)
    {
        if (!measurement.Available || !(policy.AudioLoudnessGateEnabled || policy.AudioClippingGateEnabled)) return measurement;
        if (source is not { Measured: true, Error: null } || candidate is not { Measured: true, Error: null })
            return PerceptualSearchMeasurement.Unavailable(source?.Error ?? candidate?.Error ?? "Audio loudness/peak evidence is unavailable.");
        double? drift = null;
        var loudnessPassed = true;
        var clippingPassed = true;
        if (policy.AudioLoudnessGateEnabled)
        {
            if (source.IntegratedLufs is not { } a || candidate.IntegratedLufs is not { } b || !double.IsFinite(a) || !double.IsFinite(b)
                || !double.IsFinite(policy.MaxLoudnessDriftLufs) || policy.MaxLoudnessDriftLufs < 0)
                return PerceptualSearchMeasurement.Unavailable("Complete finite loudness evidence and a valid drift target are required.");
            drift = Math.Abs(a - b);
            loudnessPassed = drift <= policy.MaxLoudnessDriftLufs;
        }
        if (policy.AudioClippingGateEnabled)
        {
            if (!source.HasTruePeak || !candidate.HasTruePeak
                || !double.IsFinite(policy.MaxTruePeakDbtp))
                return PerceptualSearchMeasurement.Unavailable("Complete finite true-peak evidence and a valid ceiling are required.");
            clippingPassed = !TruePeakSafety.Introduced(source.TruePeakDbtp, source.TruePeakIsSilent,
                candidate.TruePeakDbtp, candidate.TruePeakIsSilent, policy.MaxTruePeakDbtp);
        }
        return measurement with
        {
            MeetsTarget = measurement.MeetsTarget && loudnessPassed && clippingPassed,
            LoudnessDriftLufs = drift, SourceTruePeakDbtp = source.TruePeakDbtp, CandidateTruePeakDbtp = candidate.TruePeakDbtp,
            SourceTruePeakIsSilent = source.TruePeakIsSilent, CandidateTruePeakIsSilent = candidate.TruePeakIsSilent,
            TargetDetail = !loudnessPassed ? "The sample exceeded the configured loudness drift."
                : !clippingPassed ? "The sample introduced clipping above the configured ceiling." : measurement.TargetDetail
        };
    }
}
