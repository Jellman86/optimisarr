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

/// <summary>A trimmed output's edge silence at the trim's own level and at a more lenient one.</summary>
public sealed record TrimmedEdges(EdgeSilence Strict, EdgeSilence Lenient);

/// <summary>
/// The samples of a music file to keep once silence is trimmed from its start and end, and the level
/// that counted as silence. Stored on the job for the length of one attempt and handed to
/// verification, which expects exactly this length and measures the output at the same level. A null
/// <see cref="EndSample"/> keeps everything to the end of the decoded audio, so a file whose end is
/// not trimmed can never lose audio its container did not account for.
/// </summary>
public sealed record AudioTrimPlan(long StartSample, long? EndSample, int SampleRate, double SourceSeconds, double ThresholdDb)
{
    public double LeadingSeconds => (double)StartSample / SampleRate;
    public double KeptSeconds => (EndSample is { } end ? (double)end / SampleRate : SourceSeconds) - LeadingSeconds;
    public double TrailingSeconds => EndSample is { } end ? Math.Max(0, SourceSeconds - (double)end / SampleRate) : 0;
    public bool TrimsStart => StartSample > 0;
    public bool TrimsEnd => EndSample is not null;

    /// <summary>
    /// Whole samples rather than seconds, so the cut does not depend on how a float is printed, and
    /// timestamps restarted so the kept audio begins at zero.
    /// </summary>
    public string FilterExpression => string.Create(CultureInfo.InvariantCulture,
        $"atrim=start_sample={StartSample}{(EndSample is { } end ? $":end_sample={end}" : "")},asetpts=PTS-STARTPTS");

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{StartSample}:{EndSample?.ToString(CultureInfo.InvariantCulture) ?? "end"}:{SampleRate}:{SourceSeconds:R}:{ThresholdDb:R}");

    public static AudioTrimPlan? TryParse(string? value)
    {
        var parts = value?.Split(':');
        if (parts is not { Length: 5 }
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var rate)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var source)
            || !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold)
            || rate <= 0 || !double.IsFinite(source) || source <= 0 || !SilenceTrimSettings.ValidThreshold(threshold))
            return null;
        long? end = null;
        if (parts[1] != "end")
        {
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= start) return null;
            end = parsed;
        }
        return start > 0 || end is not null ? new AudioTrimPlan(start, end, rate, source, threshold) : null;
    }
}

/// <summary>
/// What one <c>silencedetect</c> pass heard: its silent stretches, and how long the decoded audio
/// really was. That length comes from the pass itself rather than the container, which can be
/// wrong in either direction (a chained Ogg file reports a fraction of its audio; VBR MP3 without a
/// header can report far more).
/// </summary>
public sealed record SilenceMeasurement(IReadOnlyList<SilenceInterval> Intervals, double DecodedSeconds);

/// <summary>Reads what FFmpeg's <c>silencedetect</c> filter and <c>-progress</c> report on stderr.</summary>
public static partial class SilenceDetectParser
{
    // FFmpeg 6.1 reports, for example:
    //   [silencedetect @ 0x55…] silence_start: 7
    //   [silencedetect @ 0x55…] silence_end: 8.50002 | silence_duration: 1.50002
    //   out_time_us=13688163
    // A silence that runs to the end of the file is closed at the end; an unclosed start is still
    // treated as running to the end in case another build does not. The progress time is where the
    // last frame starts, so a silence closed later than it is the better measure of the end.
    // Anchored to the filter's own log prefix: FFmpeg echoes a file's tags to stderr, and a lyrics tag
    // that happened to contain "silence_start: 0" must not invent a silence.
    [GeneratedRegex(@"^\[silencedetect @ [^\]]*\] silence_start:\s*(-?[0-9.eE+-]+)")]
    private static partial Regex StartLine();

    [GeneratedRegex(@"^\[silencedetect @ [^\]]*\] silence_end:\s*(-?[0-9.eE+-]+)")]
    private static partial Regex EndLine();

    [GeneratedRegex(@"^out_time_us=(\d+)\s*$")]
    private static partial Regex OutTime();

    /// <summary>Null when the pass never said how far it decoded, so nothing can be trusted.</summary>
    public static SilenceMeasurement? Parse(string stderr)
    {
        var raw = new List<(double Start, double? End)>();
        double? decoded = null;
        foreach (var line in stderr.Split('\n'))
        {
            if (StartLine().Match(line.TrimEnd('\r')) is { Success: true } start && TryRead(start, out var startAt))
                raw.Add((Math.Max(0, startAt), null));
            else if (EndLine().Match(line.TrimEnd('\r')) is { Success: true } end && TryRead(end, out var endAt)
                && raw.Count > 0 && raw[^1].End is null && endAt > raw[^1].Start)
                raw[^1] = (raw[^1].Start, endAt);
            else if (OutTime().Match(line.Trim()) is { Success: true } time
                && long.TryParse(time.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var micros) && micros > 0)
                decoded = micros / 1_000_000.0;
        }
        if (decoded is not { } progress) return null;
        var length = Math.Max(progress, raw.Select(interval => interval.End ?? 0).DefaultIfEmpty(0).Max());
        return new SilenceMeasurement(
            [.. raw.Select(interval => new SilenceInterval(interval.Start, interval.End ?? length)).Where(interval => interval.End > interval.Start)],
            length);
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
        // The progress report says how far the pass really decoded; see SilenceMeasurement.
        "-hide_banner", "-nostats", "-nostdin", "-progress", "pipe:2",
        "-i", inputPath,
        "-map", "0:a:0",
        "-af", string.Create(CultureInfo.InvariantCulture, $"silencedetect=noise={thresholdDb:0.##}dB:d={minimumSeconds:0.###}"),
        "-f", "null", "-",
    ];
}

/// <summary>The library settings for trimming silence: their defaults and the ranges a library may save.</summary>
public static class SilenceTrimSettings
{
    public const double DefaultThresholdDb = -60;
    public const double DefaultMinimumSeconds = 2;
    public const double MinimumThresholdDb = -90;
    public const double MaximumThresholdDb = -30;
    public const double ShortestMinimumSeconds = 0.5;
    public const double LongestMinimumSeconds = 30;

    public const string RangeError =
        "Silence is quieter than a threshold between -90 and -30 dB, lasting between 0.5 and 30 seconds.";

    public static bool ValidThreshold(double db) => double.IsFinite(db) && db is >= MinimumThresholdDb and <= MaximumThresholdDb;

    public static bool ValidMinimum(double seconds) =>
        double.IsFinite(seconds) && seconds is >= ShortestMinimumSeconds and <= LongestMinimumSeconds;
}

/// <summary>A planned trim, or why there is none.</summary>
public sealed record SilenceTrimDecision(AudioTrimPlan? Plan, string Reason);

/// <summary>What is known about a music file before its silence is measured.</summary>
public sealed record SilenceTrimSource(
    bool Probed,
    double? ContainerSeconds,
    int AudioStreamCount,
    int SampleRate,
    // Something timed from the start of the file: synchronised lyrics, a cue sheet, chapters or a
    // sidecar .lrc file. Cutting the start would shift every one of those times.
    bool StartIsTimed);

/// <summary>
/// Turns measured silence into the window to keep. Only silence touching the very start or the
/// very end of the file counts: a pause inside the track, and a hidden track after a long gap, are
/// never cut, because neither reaches an edge. A little of each trimmed silence is kept so an
/// attack or a fading tail is not clipped by a detector that is only accurate to a few milliseconds.
/// </summary>
public static partial class SilenceTrimPlanner
{
    /// <summary>Silence beginning or ending this close to an edge is that edge's silence.</summary>
    public const double EdgeToleranceSeconds = 0.05;

    /// <summary>Silence kept at each trimmed edge.</summary>
    public const double PaddingSeconds = 0.25;

    /// <summary>Less audio than this left over means the detector heard the music as silence.</summary>
    public const double MinimumKeptSeconds = 1.0;

    /// <summary>
    /// Further apart than this, the container's length and the decoded audio disagree about the file,
    /// and a trim measured on one would be cut against the other. The decoded length is short of the
    /// true end by up to one audio frame, which this allows for.
    /// </summary>
    public const double LengthAgreementSeconds = 0.15;

    /// <summary>
    /// Tags whose times count from the start of the file: a cue sheet, or lyrics (LYRICS,
    /// SYNCEDLYRICS, UNSYNCEDLYRICS and the like) with <c>[mm:ss]</c> timestamps.
    /// </summary>
    public static bool HasStartTimedTags(IReadOnlyDictionary<string, string> tags) =>
        tags.Any(tag => tag.Key.Equals("cuesheet", StringComparison.OrdinalIgnoreCase)
            || tag.Key.Contains("lyrics", StringComparison.OrdinalIgnoreCase) && TimedLyric().IsMatch(tag.Value));

    [GeneratedRegex(@"\[\d{1,3}:\d{2}")]
    private static partial Regex TimedLyric();

    public static EdgeSilence Edges(SilenceMeasurement measurement)
    {
        var length = measurement.DecodedSeconds;
        var leading = measurement.Intervals.FirstOrDefault(interval => interval.Start <= EdgeToleranceSeconds);
        var trailing = measurement.Intervals.LastOrDefault(interval => interval.End >= length - EdgeToleranceSeconds);
        return new EdgeSilence(
            leading is null ? 0 : leading.End,
            trailing is null ? 0 : Math.Max(0, length - trailing.Start));
    }

    /// <summary>Every doubt about the file resolves to no trim, with the reason.</summary>
    public static SilenceTrimDecision Decide(SilenceTrimSource source, SilenceMeasurement? measurement, double thresholdDb)
    {
        if (!source.Probed || source.ContainerSeconds is not > 0)
            return new(null, "The file could not be probed, so nothing was trimmed.");
        if (source.AudioStreamCount != 1)
            return new(null, "The file has more than one audio stream, so nothing was trimmed.");
        if (measurement is null)
            return new(null, "The file's silence could not be measured, so nothing was trimmed.");
        if (Math.Abs(measurement.DecodedSeconds - source.ContainerSeconds.Value) > LengthAgreementSeconds)
            return new(null, string.Create(CultureInfo.InvariantCulture,
                $"The file decodes to {measurement.DecodedSeconds:0.##} s but says it is {source.ContainerSeconds.Value:0.##} s long, so nothing was trimmed."));
        return Plan(measurement, source.SampleRate, thresholdDb, trimStart: !source.StartIsTimed);
    }

    /// <param name="trimStart">False when something in the file is timed from its start.</param>
    public static SilenceTrimDecision Plan(SilenceMeasurement measurement, int sampleRate, double thresholdDb, bool trimStart = true)
    {
        var length = measurement.DecodedSeconds;
        if (!(length > 0) || sampleRate <= 0)
            return new(null, "The file's length or sample rate is unknown.");

        var edges = Edges(measurement);
        var leading = trimStart ? Math.Max(0, edges.Leading - PaddingSeconds) : 0;
        var trailing = Math.Max(0, edges.Trailing - PaddingSeconds);
        if (leading <= 0 && trailing <= 0)
            return new(null, "No silence at the start or end.");
        if (length - leading - trailing < MinimumKeptSeconds)
            return new(null, "Almost all of the file is silent, so nothing was trimmed.");

        // Floor at the start and ceiling at the end both keep a sample rather than lose one.
        var startSample = (long)Math.Floor(leading * sampleRate);
        long? endSample = trailing > 0 ? (long)Math.Ceiling((length - trailing) * sampleRate) : null;
        return new(new AudioTrimPlan(startSample, endSample, sampleRate, length, thresholdDb),
            string.Create(CultureInfo.InvariantCulture, $"Trims {leading:0.##} s from the start and {trailing:0.##} s from the end."));
    }
}
