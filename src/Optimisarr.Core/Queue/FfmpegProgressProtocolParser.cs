using System.Globalization;

namespace Optimisarr.Core.Queue;

/// <summary>
/// Stateful parser for the newline-delimited key/value blocks emitted by FFmpeg's
/// <c>-progress</c> protocol. A sample is returned only at a <c>progress=continue</c> or
/// <c>progress=end</c> boundary, after which all fields are reset so a partial block cannot reuse
/// stale telemetry. Unknown keys and malformed values are deliberately ignored.
/// </summary>
public sealed class FfmpegProgressProtocolParser
{
    private double? _elapsedTimestampSeconds;
    private double? _elapsedMicroseconds;
    private double? _fps;
    private double? _speed;

    public FfmpegProgressSample? ParseLine(string line)
    {
        var separator = line.IndexOf('=');
        if (separator < 0)
        {
            return null;
        }

        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim();
        switch (key)
        {
            case "out_time":
                _elapsedTimestampSeconds = TryParseTimestamp(value);
                break;
            case "out_time_us":
            case "out_time_ms":
                // Despite its legacy name, FFmpeg documents out_time_ms in microseconds. Newer
                // builds also emit the unambiguous out_time_us key with the same unit.
                _elapsedMicroseconds = TryParseNumber(value) is { } microseconds
                    ? microseconds / 1_000_000
                    : null;
                break;
            case "fps":
                _fps = TryParseNumber(value);
                break;
            case "speed":
                _speed = TryParseSpeed(value);
                break;
            case "progress" when value is "continue" or "end":
                var sample = new FfmpegProgressSample(
                    _elapsedTimestampSeconds ?? _elapsedMicroseconds,
                    _fps,
                    _speed);
                Reset();
                return sample;
        }

        return null;
    }

    private void Reset()
    {
        _elapsedTimestampSeconds = null;
        _elapsedMicroseconds = null;
        _fps = null;
        _speed = null;
    }

    private static double? TryParseTimestamp(string value)
    {
        var parts = value.Split(':');
        if (parts.Length != 3
            || !double.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
            || !double.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || hours < 0
            || minutes is < 0 or >= 60
            || seconds is < 0 or >= 60)
        {
            return null;
        }

        return (hours * 3600) + (minutes * 60) + seconds;
    }

    private static double? TryParseSpeed(string value)
    {
        var number = value.EndsWith('x') ? value[..^1].TrimEnd() : value;
        return TryParseNumber(number);
    }

    private static double? TryParseNumber(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
        && double.IsFinite(parsed)
        && parsed >= 0
            ? parsed
            : null;
}
