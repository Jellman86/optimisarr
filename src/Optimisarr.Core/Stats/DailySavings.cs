namespace Optimisarr.Core.Stats;

public readonly record struct SavingsEntry(DateTimeOffset FinishedAt, long SourceBytes, long OutputBytes);

public sealed record DailySaving(DateOnly Date, long BytesSaved, int Files);

/// <summary>Space saved per day, bucketed by the viewer's local calendar day.</summary>
public static class DailySavings
{
    public static IReadOnlyList<DailySaving> Bucket(
        IEnumerable<SavingsEntry> entries, DateTimeOffset nowUtc, int days, TimeSpan utcOffset)
    {
        var today = DateOnly.FromDateTime(nowUtc.ToOffset(utcOffset).DateTime);
        var first = today.AddDays(1 - days);
        var totals = new Dictionary<DateOnly, (long Bytes, int Files)>();
        foreach (var entry in entries)
        {
            // A candidate that grew saved nothing, so it neither adds nor subtracts.
            var saved = entry.SourceBytes - entry.OutputBytes;
            if (saved <= 0) continue;
            var day = DateOnly.FromDateTime(entry.FinishedAt.ToOffset(utcOffset).DateTime);
            if (day < first || day > today) continue;
            var current = totals.GetValueOrDefault(day);
            totals[day] = (current.Bytes + saved, current.Files + 1);
        }

        return Enumerable.Range(0, days)
            .Select(offset => first.AddDays(offset))
            .Select(day => totals.TryGetValue(day, out var total)
                ? new DailySaving(day, total.Bytes, total.Files)
                : new DailySaving(day, 0, 0))
            .ToList();
    }
}
