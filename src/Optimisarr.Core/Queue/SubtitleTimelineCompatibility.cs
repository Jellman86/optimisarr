using System.Globalization;

namespace Optimisarr.Core.Queue;

/// <summary>MP4 timed text cannot preserve simultaneous or overlapping cues in one track.</summary>
public static class SubtitleTimelineCompatibility
{
    public static bool RequiresMatroska(string? packets, IReadOnlyCollection<int> keptStreamIndexes)
    {
        var scan = new SubtitleTimelineAccumulator(keptStreamIndexes);
        foreach (var line in (packets ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            scan.AddLine(line);
        return scan.RequiresMatroska;
    }
}

internal sealed class SubtitleTimelineAccumulator(IReadOnlyCollection<int> keptStreamIndexes)
{
    private readonly HashSet<int> _kept = [.. keptStreamIndexes];
    private readonly Dictionary<int, (double Start, double End)> _previous = [];
    private bool _incompatible;

    public bool ProvedIncompatible => _incompatible;
    public bool RequiresMatroska => _incompatible || _previous.Count != _kept.Count;

    public void AddLine(string line)
    {
        if (_incompatible || string.IsNullOrWhiteSpace(line)) return;
        var fields = line.Split(',', StringSplitOptions.TrimEntries);
        if (fields.Length == 0 || !int.TryParse(fields[0], out var index) || !_kept.Contains(index)) return;
        if (fields.Length < 3 || !Seconds(fields[1], out var start) || !Seconds(fields[2], out var duration)
            || duration <= 0 || !double.IsFinite(start + duration))
        {
            _incompatible = true;
            return;
        }
        if (_previous.TryGetValue(index, out var previous)
            && (start <= previous.Start || start < previous.End - 0.000001))
        {
            _incompatible = true;
            return;
        }
        _previous[index] = (start, start + duration);
    }

    private static bool Seconds(string value, out double seconds) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
        && double.IsFinite(seconds);
}
