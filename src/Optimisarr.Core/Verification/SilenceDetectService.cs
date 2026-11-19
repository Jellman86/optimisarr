using Optimisarr.Core.Queue;

namespace Optimisarr.Core.Verification;

/// <summary>
/// Measures the silence in a music file with FFmpeg's <c>silencedetect</c>. Before the encode it
/// finds what may be trimmed; after it, how much silence the output really starts and ends with.
/// Every failure is null, so a file that cannot be measured is never trimmed, and a trimmed output
/// that cannot be measured fails its check rather than passing it.
/// </summary>
public class SilenceDetectService(string ffmpeg)
{
    /// <summary>Short enough to see the quarter of a second a trim keeps at each edge.</summary>
    public const double OutputEdgeMinimumSeconds = 0.1;

    public virtual async Task<IReadOnlyList<SilenceInterval>?> DetectAsync(
        string inputPath, double durationSeconds, double thresholdDb, double minimumSeconds, CancellationToken cancellationToken)
    {
        var stderr = await FfmpegStderr.RunAsync(
            ffmpeg, SilenceDetectCommandBuilder.Build(inputPath, thresholdDb, minimumSeconds), cancellationToken);
        return stderr is null ? null : SilenceDetectParser.ParseAll(stderr, durationSeconds);
    }

    public virtual async Task<EdgeSilence?> MeasureEdgesAsync(
        string inputPath, double durationSeconds, double thresholdDb, CancellationToken cancellationToken) =>
        await DetectAsync(inputPath, durationSeconds, thresholdDb, OutputEdgeMinimumSeconds, cancellationToken) is { } intervals
            ? SilenceTrimPlanner.Edges(intervals, durationSeconds)
            : null;
}
