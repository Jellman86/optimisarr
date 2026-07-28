using System.Globalization;
using System.Text.RegularExpressions;

namespace Optimisarr.Core.Queue;

/// <summary>A normalised progress reading from FFmpeg's human or machine-readable output.</summary>
public sealed record FfmpegProgressSample(
    double? ElapsedSeconds,
    double? Fps,
    double? Speed,
    long? Frame = null);

/// <summary>
/// Pure parser for FFmpeg's stderr progress lines (<c>time=…</c>, <c>fps=…</c>,
/// <c>speed=…x</c>). Kept free of process/IO so it is deterministic and unit
/// tested without invoking FFmpeg.
/// </summary>
public static partial class FfmpegProgressParser
{
    [GeneratedRegex(@"time=\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)")]
    private static partial Regex TimePattern();

    [GeneratedRegex(@"fps=\s*(\d+(?:\.\d+)?)")]
    private static partial Regex FpsPattern();

    [GeneratedRegex(@"speed=\s*(\d+(?:\.\d+)?)\s*x")]
    private static partial Regex SpeedPattern();

    public static FfmpegProgressSample Parse(string line) =>
        new(ParseElapsed(line), ParseNumber(FpsPattern(), line), ParseNumber(SpeedPattern(), line));

    /// <summary>
    /// Estimates wall-clock seconds remaining: the un-encoded media duration divided
    /// by the current encode speed. Null when it cannot be estimated (unknown
    /// duration or a non-positive speed), and clamped to zero at or past the end.
    /// </summary>
    public static double? EstimateRemainingSeconds(double durationSeconds, double elapsedSeconds, double speed)
    {
        if (durationSeconds <= 0 || speed <= 0)
        {
            return null;
        }

        var remainingMedia = durationSeconds - elapsedSeconds;
        return remainingMedia <= 0 ? 0 : remainingMedia / speed;
    }

    private static double? ParseElapsed(string line)
    {
        var match = TimePattern().Match(line);
        if (!match.Success)
        {
            return null;
        }

        var hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var seconds = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        return (hours * 3600) + (minutes * 60) + seconds;
    }

    private static double? ParseNumber(Regex pattern, string line)
    {
        var match = pattern.Match(line);
        return match.Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }
}

/// <summary>
/// Calculates media progress from both clocks FFmpeg exposes. Copied streams can pin
/// <c>out_time</c> at zero while video frames continue to encode, so the furthest valid clock is
/// authoritative. Values remain below one until the process itself reports completion.
/// </summary>
public static class FfmpegProgressCalculator
{
    public static double? Calculate(
        double? durationSeconds,
        int? expectedFrameCount,
        FfmpegProgressSample sample)
    {
        var timestampProgress = durationSeconds is > 0 && sample.ElapsedSeconds is { } elapsed
            ? elapsed / durationSeconds.Value
            : (double?)null;
        var frameProgress = expectedFrameCount is > 0 && sample.Frame is { } frame
            ? (double)frame / expectedFrameCount.Value
            : (double?)null;

        if (timestampProgress is null && frameProgress is null)
        {
            return null;
        }

        return Math.Clamp(
            Math.Max(timestampProgress ?? 0, frameProgress ?? 0),
            0,
            0.999);
    }

    public static int? ExpectedFramesForWindow(
        int? sourceFrameCount,
        double? sourceDurationSeconds,
        double? windowDurationSeconds,
        double? sourceFrameRate = null)
    {
        if (windowDurationSeconds is not > 0)
        {
            return null;
        }

        if (sourceFrameCount is > 0 && sourceDurationSeconds is > 0)
        {
            var boundedDuration = Math.Min(sourceDurationSeconds.Value, windowDurationSeconds.Value);
            return Math.Max(1, (int)Math.Round(
                sourceFrameCount.Value * boundedDuration / sourceDurationSeconds.Value,
                MidpointRounding.AwayFromZero));
        }

        return sourceFrameRate is > 0 and < 1_000
            ? Math.Max(1, (int)Math.Round(
                sourceFrameRate.Value * windowDurationSeconds.Value,
                MidpointRounding.AwayFromZero))
            : null;
    }
}
