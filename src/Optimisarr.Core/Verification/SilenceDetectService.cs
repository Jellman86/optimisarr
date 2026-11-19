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

    /// <summary>
    /// How much louder the second, lenient pass on an output allows silence to be. Lossy codecs
    /// smear a little noise into silence; at this level it still counts, so a kept quarter second is
    /// not mistaken for music, while real music is far louder still.
    /// </summary>
    public const double LenientMarginDb = 10;

    public virtual async Task<SilenceMeasurement?> DetectAsync(
        string inputPath, double thresholdDb, double minimumSeconds, CancellationToken cancellationToken)
    {
        var stderr = await FfmpegStderr.RunAsync(
            ffmpeg, SilenceDetectCommandBuilder.Build(inputPath, thresholdDb, minimumSeconds), cancellationToken);
        return stderr is null ? null : SilenceDetectParser.Parse(stderr);
    }

    /// <summary>
    /// The output's edge silence measured twice: at the trim's own level, which bounds how much it may
    /// keep, and at a lenient level, which bounds how little. Null when either pass fails.
    /// </summary>
    public virtual async Task<TrimmedEdges?> MeasureEdgesAsync(string inputPath, double thresholdDb, CancellationToken cancellationToken)
    {
        var strict = await DetectAsync(inputPath, thresholdDb, OutputEdgeMinimumSeconds, cancellationToken);
        var lenient = await DetectAsync(inputPath, Math.Min(SilenceTrimSettings.MaximumThresholdDb, thresholdDb + LenientMarginDb),
            OutputEdgeMinimumSeconds, cancellationToken);
        return strict is null || lenient is null
            ? null
            : new TrimmedEdges(SilenceTrimPlanner.Edges(strict), SilenceTrimPlanner.Edges(lenient));
    }
}
