using Optimisarr.Core.Domain;
using Optimisarr.Core.Verification;

namespace Optimisarr.Core.Queue;

/// <summary>A previous attempt supplies a fidelity minimum, never measurements for this encoder run.</summary>
public static class PerceptualExecutionPlan
{
    public static PerceptualQualityPlan Create(MediaKind kind, string encoder, int baseline, VerificationPolicy policy,
        PerceptualSearchSummary? previous, string? currentSourceSha256, int retryCount)
    {
        if (retryCount is < 0 or > 1 || (retryCount == 1) != (previous?.RetryValue is not null))
            throw new ArgumentException("The higher-quality retry has no valid retained intent. Create a fresh attempt.");
        int? minimum = null;
        if (previous?.RetryValue is { } retry)
        {
            var unchangedTarget = kind == MediaKind.Image
                ? policy.ImagePerceptualGateEnabled && ImagePerceptualQualityGate.ValidLimit(policy.MinimumImagePerceptualScore)
                    && previous.MinimumImageScore == policy.MinimumImagePerceptualScore
                : kind == MediaKind.Audio && policy.AudioQualityGateEnabled && AudioQualityGate.ValidLimit(policy.MaximumAudioQualityDistance)
                    && previous.MaximumAudioDistance == policy.MaximumAudioQualityDistance;
            if (previous.Kind != kind || previous.Encoder != encoder || !unchangedTarget
                || previous.PreparationBlocked || previous.Result?.Decision is not { FellBack: false } decision
                || retry <= decision.SelectedValue || !Hash(previous.SourceSha256))
                throw new ArgumentException("The saved higher-quality retry no longer matches this encoder and quality target. Create a fresh attempt.");
            // Remote planning has no transferred hash yet; its contract retains the required
            // identity and the worker validates it before measuring. Local work already has it.
            if (currentSourceSha256 is not null && (!Hash(currentSourceSha256)
                || !string.Equals(previous.SourceSha256, currentSourceSha256, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("The original changed before its higher-quality retry. Create a fresh attempt.");
            minimum = retry;
        }
        return kind switch
        {
            MediaKind.Image => PerceptualQualityPlan.Image(encoder, baseline, minimum),
            MediaKind.Audio => PerceptualQualityPlan.Audio(encoder, baseline, minimum),
            _ => throw new ArgumentException("Perceptual audio/image search requires a supported media kind.")
        };
    }

    private static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
