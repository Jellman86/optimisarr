using System.Globalization;
using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Verification;

/// <summary>Every measured channel and window must pass. Missing evidence never permits replacement.</summary>
public static class AudioQualityGate
{
    public const string CheckName = "Perceptual audio quality (Zimtohrli)";

    public static bool ValidLimit(double? limit) => limit is { } value && double.IsFinite(value) && value is >= 0 and <= 1;

    public static VerificationReport Apply(VerificationReport report, MediaKind kind, VerificationPolicy policy,
        bool preview = false, bool soundtrack = false)
    {
        if (preview || !policy.AudioQualityGateEnabled || kind != MediaKind.Audio) return report;
        var check = Evaluate(report.AudioQuality, policy.MaximumAudioQualityDistance, soundtrack);
        return report with
        {
            Checks = [.. report.Checks, check],
            AudioQuality = report.AudioQuality is { } audio ? audio with
            {
                GateEnabled = true,
                MaximumDistance = policy.MaximumAudioQualityDistance,
                GatePassed = check.Outcome == CheckOutcome.Passed
            } : null
        };
    }

    private static VerificationCheck Evaluate(AudioQualityReport? report, double? limit, bool soundtrack)
    {
        if (!ValidLimit(limit)) return Failed("Set a maximum audio difference between 0 and 1 before enabling this gate.");
        if (report is null) return Failed("Audio quality was not measured. Replacement is blocked.");
        var evidence = report.Evidence;
        var error = report?.UnavailableReason ?? AudioQualityReporting.Validate(evidence,
            evidence?.Assessment?.ReferenceSha256, evidence?.Assessment?.CandidateSha256,
            soundtrack ? AudioQualityResultParser.SoundtrackPreparation : AudioQualityResultParser.Preparation);
        if (error is not null) return Failed(error + " Replacement is blocked because audio quality could not be verified.");
        // Recompute from validated channel values rather than trusting a supplied pooled score.
        var distance = evidence!.Assessment.Windows.SelectMany(window => window.Distances.ChannelDistances).Max();
        var detail = string.Create(CultureInfo.InvariantCulture,
            $"Largest measured audio difference {distance:G}; maximum allowed {limit:G}.");
        return distance <= limit!.Value
            ? new(CheckName, CheckOutcome.Passed, detail)
            : Failed(detail + " Above the selected limit. The original is unchanged.");
    }

    private static VerificationCheck Failed(string detail) => new(CheckName, CheckOutcome.Failed, detail);
}
