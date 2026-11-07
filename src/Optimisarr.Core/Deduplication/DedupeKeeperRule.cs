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
    /// <summary>Every copy has to stay.</summary>
    NothingToMove,
}

/// <summary>One copy in a group of byte-identical files, as the rule needs to see it.</summary>
public sealed record DedupeCopy(int MediaFileId, string RelativePath, string LibraryType, int? HardLinkCount, bool Tracked, bool PossiblyTracked);

/// <summary>What the rule decided for a group. A refusal has no copies to move.</summary>
public sealed record DedupeCleanupDecision(IReadOnlyList<int> KeepIds, IReadOnlyList<int> SpareIds, DedupeCleanupRefusal? Refusal)
{
    public static DedupeCleanupDecision Refused(DedupeCleanupRefusal refusal) => new([], [], refusal);
}

/// <summary>
/// Chooses which identical copies stay. The bytes are the same, so only the path matters: a copy a
/// manager tracks stays, because moving it would show as missing. A copy with other hard links stays,
/// because moving it frees nothing. An operator's saved choice is honoured. Otherwise one copy is
/// kept by the shallowest, then shortest, path. Anything unclear leaves the whole group alone.
/// </summary>
public static class DedupeKeeperRule
{
    public const string IgnoreChoice = "Ignore";
    public const string KeepChoice = "Keep";

    private static readonly string[] AutomaticLibraryTypes = ["Film", "Tv"];

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

        // Choosing by hand says these are the same item. Without that, a different name may be a
        // different item that merely has the same bytes.
        if (chosen.Count == 0 && copies.Select(copy => ComparableName(copy.RelativePath)).Distinct(StringComparer.Ordinal).Count() > 1)
            return DedupeCleanupDecision.Refused(DedupeCleanupRefusal.NamesDiffer);

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

    private static string ComparableName(string relativePath) =>
        Path.GetFileNameWithoutExtension(relativePath.Replace('\\', '/').Split('/')[^1])
            .Normalize(NormalizationForm.FormC).ToUpper(CultureInfo.InvariantCulture);
}
