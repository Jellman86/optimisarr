namespace Optimisarr.Core.Verification;

/// <summary>
/// Everything the pure <see cref="VerificationEvaluator"/> needs to judge a
/// converted output: the outcome of the full-decode health check, the output's
/// own ffprobe result, and the original's properties to compare against. The
/// composition layer gathers these by running ffmpeg/ffprobe; the evaluation
/// itself stays pure and deterministic.
/// </summary>
public sealed record VerificationInput(
    bool DecodeSucceeded,
    string? DecodeError,
    bool OutputProbeSucceeded,
    string? OutputProbeError,
    string? OutputVideoCodec,
    long OriginalSizeBytes,
    long OutputSizeBytes,
    double? OriginalDurationSeconds,
    double? OutputDurationSeconds,
    int OriginalAudioTrackCount,
    int OutputAudioTrackCount,
    int OriginalSubtitleTrackCount,
    int OutputSubtitleTrackCount,
    bool QualityMeasured = false,
    string? QualityError = null,
    QualityScores? QualityScores = null);
