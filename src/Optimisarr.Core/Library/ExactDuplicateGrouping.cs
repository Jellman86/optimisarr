namespace Optimisarr.Core.Library;

public sealed record ExactDuplicateFile(int Id, string RelativePath, long SizeBytes, string Sha256,
    int? HardLinkCount, DateTimeOffset CheckedAt);
public sealed record ExactDuplicateGroup(string Sha256, long SizeBytes,
    IReadOnlyList<ExactDuplicateFile> Copies, long? ExtraCopyBytes, int TotalCopies = 0);
public sealed record ExactDuplicateDisplay(IReadOnlyList<ExactDuplicateGroup> Groups, bool Truncated);

/// <summary>Observations of full file bytes, never permission to remove a copy.</summary>
public static class ExactDuplicateGrouping
{
    public static IReadOnlyList<ExactDuplicateGroup> Group(IEnumerable<ExactDuplicateFile> files) => files
        .Where(f => f.SizeBytes > 0 && f.Sha256.Length == 64 && f.Sha256.All(Uri.IsHexDigit))
        .DistinctBy(f => f.RelativePath, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        .GroupBy(f => (f.SizeBytes, Hash: f.Sha256.ToLowerInvariant()))
        .Where(g => g.Count() > 1)
        .Select(g => new ExactDuplicateGroup(g.Key.Hash, g.Key.SizeBytes,
            g.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToArray(),
            g.All(f => f.HardLinkCount == 1) && g.Key.SizeBytes <= long.MaxValue / (g.Count() - 1)
                ? g.Key.SizeBytes * (g.Count() - 1) : null, g.Count()))
        .OrderByDescending(g => g.ExtraCopyBytes ?? 0).ThenBy(g => g.Sha256, StringComparer.Ordinal).ToArray();

    public static ExactDuplicateDisplay Limit(IReadOnlyList<ExactDuplicateGroup> groups)
    {
        var displayed = new List<ExactDuplicateGroup>();
        var remainingCopies = 2_000;
        var remainingText = 500_000;
        var truncated = false;
        foreach (var group in groups)
        {
            if (displayed.Count >= 200 || remainingCopies < 2) { truncated = true; break; }
            var copies = new List<ExactDuplicateFile>();
            foreach (var copy in group.Copies)
            {
                if (copies.Count >= 100 || copies.Count >= remainingCopies || copy.RelativePath.Length > remainingText) break;
                copies.Add(copy);
                remainingText -= copy.RelativePath.Length;
            }
            remainingCopies -= copies.Count;
            var shortened = copies.Count != group.Copies.Count;
            truncated |= shortened;
            if (copies.Count >= 2) displayed.Add(group with { Copies = copies.ToArray(), ExtraCopyBytes = shortened ? null : group.ExtraCopyBytes });
            else truncated = true;
        }
        return new(displayed, truncated);
    }
}
