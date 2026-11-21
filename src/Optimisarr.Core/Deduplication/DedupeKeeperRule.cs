using System.Globalization;
using System.Text;

namespace Optimisarr.Core.Deduplication;

/// <summary>Why a group of identical copies is left alone instead of being cleaned automatically.</summary>
public enum DedupeCleanupRefusal
{
    /// <summary>A connected manager could not say what it tracks, so no copy can be called untracked.</summary>
    ManagerUnavailable,
    /// <summary>The operator chose to ignore this group.</summary>
    Ignored,
    /// <summary>Only film and TV libraries are cleaned automatically; the same bytes can belong in two albums.</summary>
    LibraryType,
    /// <summary>A copy shares a tracked file's name without another copy clearly being the tracked one.</summary>
    PossiblyTracked,
    /// <summary>The copies have different names, so they may be meant to be separate items.</summary>
    NamesDiffer,
    /// <summary>The copies are in different libraries, which can be deliberate, as with a family library.</summary>
    SeparateLibraries,
    /// <summary>An extra, trailer, sample or similar, which can belong to several titles at once.</summary>
    SharedExtra,
    /// <summary>Every copy has to stay.</summary>
    NothingToMove,
}

/// <summary>One copy in a group of byte-identical files, as the rule needs to see it.</summary>
public sealed record DedupeCopy(int MediaFileId, int LibraryId, string RelativePath, string LibraryType, int? HardLinkCount, bool Tracked, bool PossiblyTracked);

/// <summary>What the rule decided for a group. A refusal has no copies to move.</summary>
public sealed record DedupeCleanupDecision(IReadOnlyList<int> KeepIds, IReadOnlyList<int> SpareIds, DedupeCleanupRefusal? Refusal)
{
    public static DedupeCleanupDecision Refused(DedupeCleanupRefusal refusal) => new([], [], refusal);
}

/// <summary>
/// Chooses which identical copies stay. The bytes are the same, so only the path matters: a copy a
/// manager tracks stays, because moving it would show as missing. A copy with other hard links stays,
/// because moving it frees nothing. An operator's saved choice is honoured. Otherwise one copy is
/// kept by the shallowest, then shortest, path. Identical bytes do not prove two placements are
/// interchangeable, so anything that could be deliberate needs a saved choice: another library, a
/// different name, or an extra that can belong to several titles. Anything unclear leaves the whole
/// group alone.
/// </summary>
public static class DedupeKeeperRule
{
    public const string IgnoreChoice = "Ignore";
    public const string KeepChoice = "Keep";

    private static readonly string[] AutomaticLibraryTypes = ["Film", "Tv"];

    // The folder and file names media servers use for material that sits alongside a title.
    private static readonly HashSet<string> ExtraFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "extras", "extra", "featurettes", "behind the scenes", "deleted scenes", "interviews", "scenes", "shorts",
        "trailers", "samples", "sample", "other", "others", "clips", "theme-music", "backdrops",
    };

    private static readonly string[] ExtraNames =
    [
        "trailer", "sample", "theme", "intro", "outro", "opening", "ending", "featurette", "behindthescenes",
        "deleted", "deletedscene", "interview", "scene", "short", "other", "extra", "clip", "video", "movie", "film",
    ];

    public static DedupeCleanupDecision Decide(
        IReadOnlyList<DedupeCopy> copies, bool managersAnswered, string? savedChoice = null, IReadOnlyList<int>? savedKeepIds = null)
    {
        if (!managersAnswered) return DedupeCleanupDecision.Refused(DedupeCleanupRefusal.ManagerUnavailable);
        if (savedChoice == IgnoreChoice) return DedupeCleanupDecision.Refused(DedupeCleanupRefusal.Ignored);
        if (copies.Any(copy => !AutomaticLibraryTypes.Contains(copy.LibraryType, StringComparer.Ordinal)))
            return DedupeCleanupDecision.Refused(DedupeCleanupRefusal.LibraryType);

        var anyTracked = copies.Any(copy => copy.Tracked);
        if (!anyTracked && copies.Any(copy => copy.PossiblyTracked))
            return DedupeCleanupDecision.Refused(DedupeCleanupRefusal.PossiblyTracked);

        var chosen = savedChoice == KeepChoice ? savedKeepIds ?? [] : [];
        var keep = copies.Where(copy => copy.Tracked || copy.HardLinkCount is > 1 || chosen.Contains(copy.MediaFileId))
            .Select(copy => copy.MediaFileId).ToHashSet();

        // Choosing by hand says these are the same item. Without that, these may be separate items
        // that merely have the same bytes.
        if (chosen.Count == 0)
        {
            if (copies.Select(copy => copy.LibraryId).Distinct().Count() > 1)
                return DedupeCleanupDecision.Refused(DedupeCleanupRefusal.SeparateLibraries);
            if (copies.Select(copy => ComparableName(copy.RelativePath)).Distinct(StringComparer.Ordinal).Count() > 1)
                return DedupeCleanupDecision.Refused(DedupeCleanupRefusal.NamesDiffer);
            if (copies.Any(copy => IsExtra(copy.RelativePath)))
                return DedupeCleanupDecision.Refused(DedupeCleanupRefusal.SharedExtra);
        }

        if (keep.Count == 0) keep.Add(Best(copies).MediaFileId);
        var spares = copies.Where(copy => !keep.Contains(copy.MediaFileId)).Select(copy => copy.MediaFileId).ToList();
        return spares.Count == 0
            ? DedupeCleanupDecision.Refused(DedupeCleanupRefusal.NothingToMove)
            : new DedupeCleanupDecision([.. keep.Order()], spares, null);
    }

    private static DedupeCopy Best(IReadOnlyList<DedupeCopy> copies) => copies
        .OrderBy(copy => copy.RelativePath.Replace('\\', '/').Count(character => character == '/'))
        .ThenBy(copy => copy.RelativePath.Length)
        .ThenBy(copy => copy.MediaFileId)
        .First();

    private static bool IsExtra(string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var name = ComparableName(relativePath);
        return segments[..^1].Any(ExtraFolders.Contains)
            || ExtraNames.Any(extra => string.Equals(name, extra, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("-" + extra, StringComparison.OrdinalIgnoreCase));
    }

    private static string ComparableName(string relativePath) =>
        Path.GetFileNameWithoutExtension(relativePath.Replace('\\', '/').Split('/')[^1])
            .Normalize(NormalizationForm.FormC).ToUpper(CultureInfo.InvariantCulture);
}
