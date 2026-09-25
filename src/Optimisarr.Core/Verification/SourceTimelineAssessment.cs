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
        double? outputVideoSeconds,
        double? sourceVideoMetadataSeconds = null)
    {
        if (sourceVideoSeconds is not { } source || source < 0 || !double.IsFinite(source)
            || sourceAudioSeconds is not { } audio || audio < 30 || !double.IsFinite(audio))
        {
            return false;
        }

        // Metadata alone cannot overrule a plausible packet scan. When a scan returns only the
        // opening frames, though, agreement between primary audio and either an independently
        // encoded output or the source stream's own duration makes that scan suspect. The caller
        // confirms the packet read once before using this result. Indeterminate still blocks
        // replacement; it simply does not assert that the original is corrupt.
        if (source >= audio / 2)
        {
            return false;
        }

        // A complete candidate timeline that agrees with the short source is independent
        // corroboration. Stream duration metadata must not override that stronger observation.
        if (outputVideoSeconds is { } output && output >= 30 && double.IsFinite(output)
            && Math.Abs(source - output) / Math.Max(source, output) <= 0.05)
        {
            return false;
        }

        return AgreesWithAudio(outputVideoSeconds) || AgreesWithAudio(sourceVideoMetadataSeconds);

        bool AgreesWithAudio(double? span) =>
            span is { } candidate && candidate >= 30 && double.IsFinite(candidate)
            && source < candidate / 2
            && Math.Abs(audio - candidate) / Math.Max(audio, candidate) <= 0.05;
    }
}
