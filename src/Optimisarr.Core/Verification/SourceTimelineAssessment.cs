namespace Optimisarr.Core.Verification;

/// <summary>Distinguishes a suspicious packet scan from a picture stream that really ends early.</summary>
public static class SourceTimelineAssessment
{
    /// <summary>
    /// A packet scan that ends materially before primary audio may be incomplete. Confirm it
    /// once before calling the unchanged source corrupt; ordinary aligned sources get one scan.
    /// </summary>
    public static bool NeedsConfirmation(double? sourceVideoSeconds, double? sourceAudioSeconds)
    {
        if (sourceVideoSeconds is not { } video || video < 0 || !double.IsFinite(video)
            || sourceAudioSeconds is not { } audio || audio <= 0 || !double.IsFinite(audio))
        {
            return false;
        }

        var shortfall = audio - video;
        return shortfall > 1 && shortfall / audio > 0.02;
    }

    public static bool IsIndeterminate(
        double? sourceVideoSeconds,
        double? sourceAudioSeconds,
        double? outputVideoSeconds)
    {
        if (sourceVideoSeconds is not { } source || source < 0 || !double.IsFinite(source)
            || sourceAudioSeconds is not { } audio || audio < 30 || !double.IsFinite(audio)
            || outputVideoSeconds is not { } output || output < 30 || !double.IsFinite(output))
        {
            return false;
        }

        var longerSpan = Math.Min(audio, output);
        return source < longerSpan / 2
            && Math.Abs(audio - output) / Math.Max(audio, output) <= 0.05;
    }
}
