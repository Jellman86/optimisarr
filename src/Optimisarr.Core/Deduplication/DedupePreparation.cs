using System.Security.Cryptography;
using System.Text.Json;

namespace Optimisarr.Core.Deduplication;

public sealed record DedupeFileSnapshot(int MediaFileId, int LibraryId, string Root, string Path,
    string? QuarantinePath, string Identity, long SizeBytes, long ModifiedUtcTicks, string Sha256, bool IsKeeper);
public sealed record DedupePreparation(Guid Id, int LibraryId, string ReviewKey, string ScopeKey,
    IReadOnlyList<DedupeFileSnapshot> Files);

/// <summary>Frozen rollback intent. A valid plan is not content verification or permission to move files.</summary>
public static class DedupePreparationPolicy
{
    public const string Contract = "exact-quarantine-journal-v1";
    public const string Retention = "UntilRestored";

    public static bool IsValid(DedupePreparation plan) => plan.Id != Guid.Empty && plan.LibraryId > 0
        && IsHash(plan.ReviewKey) && IsHash(plan.ScopeKey) && plan.Files.Count is >= 2 and <= 100
        && plan.Files.Any(f => f.IsKeeper) && plan.Files.Any(f => !f.IsKeeper)
        && plan.Files.Select(f => f.MediaFileId).Distinct().Count() == plan.Files.Count
        && plan.Files.All(f => f.MediaFileId > 0 && f.LibraryId > 0 && f.SizeBytes > 0 && f.ModifiedUtcTicks > 0
            && IsHash(f.Sha256) && ValidText(f.Root, 4096) && ValidText(f.Path, 4096) && ValidText(f.Identity, 160)
            && (f.IsKeeper ? f.QuarantinePath is null : ValidText(f.QuarantinePath, 4096)))
        && plan.Files.Select(f => (f.SizeBytes, f.Sha256)).Distinct().Count() == 1
        && plan.Files.Select(f => f.Identity).Distinct(StringComparer.Ordinal).Count() == plan.Files.Count;

    /// <summary>
    /// Every path is absolute and canonical inside its library root, sources are distinct, and each
    /// copy has its own quarantine destination that is not any planned source.
    /// </summary>
    public static bool HasValidPaths(DedupePreparation plan)
    {
        try
        {
            var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var sources = plan.Files.Select(f => f.Path).ToHashSet(comparer);
            var destinations = plan.Files.Where(f => !f.IsKeeper).Select(f => f.QuarantinePath!).ToArray();
            return sources.Count == plan.Files.Count && destinations.Distinct(comparer).Count() == destinations.Length
                && destinations.All(p => !sources.Contains(p)) && plan.Files.All(f =>
                    Canonical(f.Root) && Canonical(f.Path) && Within(f.Root, f.Path)
                    && (f.QuarantinePath is null || Canonical(f.QuarantinePath) && Within(f.Root, f.QuarantinePath)));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public static string Signature(DedupePreparation plan) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { Contract, Retention, plan.Id, plan.LibraryId, plan.ReviewKey,
            plan.ScopeKey, Files = plan.Files.OrderBy(f => f.MediaFileId) })));

    private static bool Canonical(string path) => Path.IsPathFullyQualified(path) && Path.GetFullPath(path) == path;

    private static bool Within(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != "." && relative != ".." && !Path.IsPathRooted(relative)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool ValidText(string? value, int maximum) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximum && !value.Contains('\0');
}
