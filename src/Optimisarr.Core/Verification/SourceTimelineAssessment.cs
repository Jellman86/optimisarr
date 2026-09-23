namespace Optimisarr.Core.Verification;

/// <summary>Distinguishes a suspicious packet scan from a picture stream that really ends early.</summary>
public static class SourceTimelineAssessment
{
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
