namespace Optimisarr.Core.Verification;

/// <summary>
/// The thresholds a converted output must satisfy before a job may move toward
/// replacement. Defaults are conservative: the output must decode cleanly, keep
/// the same duration (within tolerance), retain every audio track, and be smaller
/// than the original. Subtitle retention is not required by default because some
/// rule profiles intentionally drop image-based subtitles.
/// </summary>
public sealed record VerificationPolicy(
    double DurationTolerancePercent,
    bool RequireAudioRetained,
    bool RequireSubtitlesRetained,
    bool RequireSizeReduction)
{
    public static VerificationPolicy Default { get; } = new(
        DurationTolerancePercent: 1.0,
        RequireAudioRetained: true,
        RequireSubtitlesRetained: false,
        RequireSizeReduction: true);
}
