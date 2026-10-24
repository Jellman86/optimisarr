using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;

namespace Optimisarr.Core.Verification;

/// <summary>One higher-fidelity encode when complete final quality evidence alone missed its target.</summary>
public static class PerceptualQualityRetryPolicy
{
    public static int? Next(MediaKind kind, PerceptualQualityPlan plan, int currentValue, int retryCount,
        VerificationReport report, VerificationPolicy policy)
    {
        if (retryCount != 0) return null;
        var failed = report.Checks.Where(c => c.Outcome == CheckOutcome.Failed).ToArray();
        var name = kind == MediaKind.Image ? ImagePerceptualQualityGate.CheckName : AudioQualityGate.CheckName;
        if (failed.Length != 1 || failed[0].Name != name) return null;
        PerceptualSearchMeasurement measured;
        if (kind == MediaKind.Image && policy.ImagePerceptualGateEnabled && report.ImagePerceptualQuality is { } image)
        {
            if (image.Measurement?.MaximumAlphaError > 0.000001) return null;
            measured = PerceptualSearchEvidence.Image(image, image.SourceSha256 ?? "", image.CandidateSha256 ?? "", policy.MinimumImagePerceptualScore);
        }
        else if (kind == MediaKind.Audio && policy.AudioQualityGateEnabled && report.AudioQuality is { Evidence.Assessment: { } audio } audioReport)
            measured = PerceptualSearchEvidence.Audio(audioReport, audio.ReferenceSha256 ?? "", audio.CandidateSha256 ?? "", policy.MaximumAudioQualityDistance);
        else return null;
        if (!measured.Available || measured.MeetsTarget) return null;
        var index = plan.Values.ToList().IndexOf(currentValue);
        return index > 0 ? plan.Values[Math.Max(0, index - Math.Max(1, plan.Values.Count / 8))] : null;
    }
}
