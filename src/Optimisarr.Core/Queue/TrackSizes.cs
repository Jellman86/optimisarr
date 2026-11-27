using System.Globalization;

namespace Optimisarr.Core.Queue;

/// <summary>
/// The stored form of per-track sizes in bytes, in the same track order as the stored languages:
/// "4500000000, ?, 0". A "?" keeps an unknown size in its place, so the list still lines up with
/// the track indexes the removal rules use.
/// </summary>
public static class TrackSizes
{
    public const int MaxStoredLength = 2048;

    private const string Unknown = "?";

    /// <summary>Null when there are no tracks of this kind.</summary>
    public static string? Format(IReadOnlyList<long?> sizes) =>
        sizes.Count == 0
            ? null
            : string.Join(", ", sizes.Select(size => size?.ToString(CultureInfo.InvariantCulture) ?? Unknown));

    /// <summary>
    /// Null when nothing was stored, which is every file probed before track sizes were recorded.
    /// An entry that is not a non-negative whole number reads as unknown, never as a size.
    /// </summary>
    public static IReadOnlyList<long?>? Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        return stored.Split(',', StringSplitOptions.TrimEntries)
            .Select(entry => long.TryParse(entry, NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                ? size
                : (long?)null)
            .ToList();
    }
}
