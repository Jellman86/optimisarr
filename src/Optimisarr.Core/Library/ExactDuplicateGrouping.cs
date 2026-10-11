using System.Text.Json.Serialization;

namespace Optimisarr.Core.Library;

public sealed record ExactDuplicateAlias(int Id, int LibraryId, string LibraryName, string RelativePath, [property: JsonIgnore] string? GenerationKey);
public sealed record ExactDuplicateFile(int Id, string RelativePath, long SizeBytes, string Sha256,
    int? HardLinkCount, DateTimeOffset CheckedAt, bool Reused = false, int LibraryId = 0,
    string LibraryName = "", string LibraryType = "", [property: JsonIgnore] string? PhysicalKey = null,
    [property: JsonIgnore] string? GenerationKey = null, [property: JsonIgnore] string? AbsolutePath = null,
    IReadOnlyList<ExactDuplicateAlias>? Aliases = null);
public sealed record ExactDuplicateDecision(string Choice, IReadOnlyList<int> KeepIds, DateTimeOffset SavedAt);
public sealed record ExactDuplicateGroup(string Sha256, long SizeBytes,
    IReadOnlyList<ExactDuplicateFile> Copies, long? ExtraCopyBytes, int TotalCopies = 0,
    string? ReviewKey = null, ExactDuplicateDecision? Decision = null, bool CanDecide = false);
public sealed record ExactDuplicateDisplay(IReadOnlyList<ExactDuplicateGroup> Groups, bool Truncated);

/// <summary>Observations of full file bytes, never permission to remove a copy.</summary>
public static class ExactDuplicateGrouping
{
    public static IReadOnlyList<ExactDuplicateGroup> Group(IEnumerable<ExactDuplicateFile> files)
    {
        var valid = files.Where(f => f.SizeBytes > 0 && f.Sha256.Length == 64 && f.Sha256.All(Uri.IsHexDigit)).ToArray();
        var ambiguous = valid.Where(f => f.PhysicalKey is not null).GroupBy(f => f.PhysicalKey!)
            .Where(g => g.Select(f => (f.SizeBytes, Hash: f.Sha256.ToLowerInvariant())).Distinct().Count() > 1).Select(g => g.Key).ToHashSet();
        return valid.Where(f => f.PhysicalKey is null || !ambiguous.Contains(f.PhysicalKey))
        .GroupBy(f => (f.SizeBytes, Hash: f.Sha256.ToLowerInvariant()))
        .Select(g => g.GroupBy(f => f.PhysicalKey is not null ? "identity:" + f.PhysicalKey
            : "path:" + (f.AbsolutePath is not null ? NormalisePath(f.AbsolutePath) : $"{f.LibraryId}:{f.RelativePath}"))
            .Select(physical =>
            {
                var refs = physical.OrderBy(f => f.LibraryId).ThenBy(f => f.RelativePath, StringComparer.Ordinal).ThenBy(f => f.Id).ToArray();
                return refs[0] with { HardLinkCount = refs.Any(f => f.HardLinkCount is null) ? null : refs.Max(f => f.HardLinkCount),
                    Aliases = refs.Skip(1).Select(f => new ExactDuplicateAlias(f.Id, f.LibraryId,
                    f.LibraryName, f.RelativePath, f.GenerationKey)).ToArray() };
            }).ToArray())
        .Where(g => g.Length > 1)
        .Select(g => new ExactDuplicateGroup(g[0].Sha256.ToLowerInvariant(), g[0].SizeBytes,
            g.OrderBy(f => f.LibraryId).ThenBy(f => f.RelativePath, StringComparer.Ordinal).ToArray(),
            g.All(f => f.HardLinkCount == 1) && g[0].SizeBytes <= long.MaxValue / (g.Length - 1)
                ? g[0].SizeBytes * (g.Length - 1) : null, g.Length))
        .OrderByDescending(g => g.ExtraCopyBytes ?? 0).ThenBy(g => g.Sha256, StringComparer.Ordinal).ToArray();
    }

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
            var groupReferences = 0;
            foreach (var copy in group.Copies)
            {
                var text = copy.RelativePath.Length + copy.LibraryName.Length;
                if (groupReferences >= 100 || remainingCopies < 1 || text > remainingText) break;
                // References share the same response budgets as physical copies. A shortened group
                // cannot save a decision about paths that the reviewer has not seen.
                remainingText -= text;
                remainingCopies--; groupReferences++;
                var aliases = new List<ExactDuplicateAlias>();
                var reservedCopies = Math.Min(group.Copies.Count - copies.Count - 1, 100 - groupReferences);
                foreach (var alias in copy.Aliases ?? [])
                {
                    var aliasText = alias.RelativePath.Length + alias.LibraryName.Length;
                    if (groupReferences + reservedCopies >= 100 || remainingCopies <= reservedCopies || aliasText > remainingText) break;
                    aliases.Add(alias); remainingText -= aliasText;
                    remainingCopies--; groupReferences++;
                }
                var shortenedAliases = aliases.Count != (copy.Aliases?.Count ?? 0);
                truncated |= shortenedAliases;
                copies.Add(copy with { Aliases = aliases.ToArray() });
            }
            var shortened = copies.Count != group.Copies.Count;
            truncated |= shortened;
            var incomplete = shortened || copies.Sum(c => c.Aliases?.Count ?? 0) != group.Copies.Sum(c => c.Aliases?.Count ?? 0);
            if (copies.Count >= 2) displayed.Add(group with { Copies = copies.ToArray(), ExtraCopyBytes = incomplete ? null : group.ExtraCopyBytes,
                CanDecide = group.CanDecide && !incomplete });
            else truncated = true;
        }
        return new(displayed, truncated);
    }

    private static string NormalisePath(string path) => OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
}
