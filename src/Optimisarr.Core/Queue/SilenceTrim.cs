using System.Globalization;
using System.Text.RegularExpressions;

namespace Optimisarr.Core.Queue;

/// <summary>A stretch of audio <c>silencedetect</c> heard as silent, in seconds from the start.</summary>
public sealed record SilenceInterval(double Start, double End)
{
    public double Duration => End - Start;
}

/// <summary>How much silence a file starts and ends with, in seconds.</summary>
public sealed record EdgeSilence(double Leading, double Trailing);

/// <summary>
/// The samples of a music file to keep once silence is trimmed from its start and end. Stored on
/// the job as text so a retry encodes the same window without measuring again, and handed to
/// verification so it can expect exactly this length.
/// </summary>
public sealed record AudioTrimPlan(long StartSample, long EndSample, int SampleRate, double SourceSeconds)
{
    public double LeadingSeconds => (double)StartSample / SampleRate;
    public double KeptSeconds => (double)(EndSample - StartSample) / SampleRate;
    public double TrailingSeconds => Math.Max(0, SourceSeconds - (double)EndSample / SampleRate);
    public bool TrimsStart => StartSample > 0;
    public bool TrimsEnd => TrailingSeconds > 0;

    /// <summary>
    /// Whole samples rather than seconds, so the cut does not depend on how a float is printed, and
    /// timestamps restarted so the kept audio begins at zero.
    /// </summary>
    public string FilterExpression =>
        string.Create(CultureInfo.InvariantCulture, $"atrim=start_sample={StartSample}:end_sample={EndSample},asetpts=PTS-STARTPTS");

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{StartSample}:{EndSample}:{SampleRate}:{SourceSeconds:R}");

    public static AudioTrimPlan? TryParse(string? value)
    {
        var parts = value?.Split(':');
        return parts is { Length: 4 }
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var end)
            && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var rate)
            && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var source)
            && rate > 0 && end > start && double.IsFinite(source) && source > 0
                ? new AudioTrimPlan(start, end, rate, source)
                : null;
    }
}

/// <summary>Reads what FFmpeg's <c>silencedetect</c> filter reports on stderr.</summary>
public static partial class SilenceDetectParser
{
    // FFmpeg 6.1 reports, for example:
    //   [silencedetect @ 0x55…] silence_start: 7
    //   [silencedetect @ 0x55…] silence_end: 8.50002 | silence_duration: 1.50002
    // A silence that runs to the end of the file is closed at the end; an unclosed start is still
    // treated as running to the end in case another build does not.
    [GeneratedRegex(@"silencedetect.*\bsilence_start:\s*(-?[0-9.eE+-]+)")]
    private static partial Regex StartLine();

    [GeneratedRegex(@"silencedetect.*\bsilence_end:\s*(-?[0-9.eE+-]+)")]
    private static partial Regex EndLine();

    public static IReadOnlyList<SilenceInterval> ParseAll(string stderr, double durationSeconds)
    {
        var intervals = new List<SilenceInterval>();
        double? open = null;
        foreach (var line in stderr.Split('\n'))
        {
            if (StartLine().Match(line) is { Success: true } start && TryRead(start, out var startAt))
            {
                open = Math.Max(0, startAt);
            }
            else if (EndLine().Match(line) is { Success: true } end && TryRead(end, out var endAt) && open is { } from)
            {
                if (endAt > from) intervals.Add(new SilenceInterval(from, Math.Min(endAt, durationSeconds)));
                open = null;
            }
        }
        if (open is { } unclosed && durationSeconds > unclosed) intervals.Add(new SilenceInterval(unclosed, durationSeconds));
        return intervals;
    }

    private static bool TryRead(Match match, out double value) =>
        double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);
}

/// <summary>The FFmpeg arguments that measure a file's silence without writing anything.</summary>
public static class SilenceDetectCommandBuilder
{
    /// <summary>
    /// The first audio stream only, every channel together: a passage counts as silent only when
    /// all channels are, so one quiet channel of a stereo file is never mistaken for silence.
    /// </summary>
    public static IReadOnlyList<string> Build(string inputPath, double thresholdDb, double minimumSeconds) =>
    [
        "-hide_banner", "-nostats", "-nostdin",
        "-i", inputPath,
        "-map", "0:a:0",
        "-af", string.Create(CultureInfo.InvariantCulture, $"silencedetect=noise={thresholdDb:0.##}dB:d={minimumSeconds:0.###}"),
        "-f", "null", "-",
    ];
}

/// <summary>A planned trim, or why there is none.</summary>
public sealed record SilenceTrimDecision(AudioTrimPlan? Plan, string Reason);

/// <summary>
/// Turns measured silence into the window to keep. Only silence touching the very start or the
/// very end of the file counts: a pause inside the track, and a hidden track after a long gap, are
/// never cut, because neither reaches an edge. A little of each trimmed silence is kept so an
/// attack or a fading tail is not clipped by a detector that is only accurate to a few milliseconds.
/// </summary>
public static class SilenceTrimPlanner
{
    /// <summary>Silence beginning or ending this close to an edge is that edge's silence.</summary>
    public const double EdgeToleranceSeconds = 0.05;

    /// <summary>Silence kept at each trimmed edge.</summary>
    public const double PaddingSeconds = 0.25;

    /// <summary>Less audio than this left over means the detector heard the music as silence.</summary>
    public const double MinimumKeptSeconds = 1.0;

    public static EdgeSilence Edges(IReadOnlyList<SilenceInterval> intervals, double durationSeconds)
    {
        var leading = intervals.FirstOrDefault(interval => interval.Start <= EdgeToleranceSeconds);
        var trailing = intervals.LastOrDefault(interval => interval.End >= durationSeconds - EdgeToleranceSeconds);
        return new EdgeSilence(
            leading is null ? 0 : leading.End,
            trailing is null ? 0 : Math.Max(0, durationSeconds - trailing.Start));
    }

    /// <param name="trimStart">
    /// False when something in the file is timed from its start, such as synchronised lyrics or a
    /// cue sheet, which cutting the start would shift. The end can still be trimmed.
    /// </param>
    public static SilenceTrimDecision Plan(
        IReadOnlyList<SilenceInterval> intervals, double durationSeconds, int sampleRate, bool trimStart = true)
    {
        if (!(durationSeconds > 0) || sampleRate <= 0)
            return new(null, "The file's length or sample rate is unknown.");

        var edges = Edges(intervals, durationSeconds);
        var leading = trimStart ? Math.Max(0, edges.Leading - PaddingSeconds) : 0;
        var trailing = Math.Max(0, edges.Trailing - PaddingSeconds);
        if (leading <= 0 && trailing <= 0)
            return new(null, "No silence at the start or end.");
        if (durationSeconds - leading - trailing < MinimumKeptSeconds)
            return new(null, "Almost all of the file is silent, so nothing was trimmed.");

        var startSample = (long)Math.Floor(leading * sampleRate);
        var endSample = (long)Math.Ceiling((durationSeconds - trailing) * sampleRate);
        return new(new AudioTrimPlan(startSample, endSample, sampleRate, durationSeconds),
            string.Create(CultureInfo.InvariantCulture, $"Trims {leading:0.##} s from the start and {trailing:0.##} s from the end."));
    }
}
