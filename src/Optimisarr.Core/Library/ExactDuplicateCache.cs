namespace Optimisarr.Core.Library;

public sealed record ExactFileGeneration(string Root, string Path, long SizeBytes, long ModifiedUtcTicks, string? Identity);
public sealed record ExactFileFingerprint(ExactFileGeneration Generation, string Sha256, DateTimeOffset CheckedAt, string Contract);

/// <summary>Metadata can invalidate a hash, but cannot establish fresh content identity.</summary>
public static class ExactDuplicateCache
{
    public const string Contract = "full-file-sha256-v1";
    public static bool CanReuse(ExactFileGeneration current, ExactFileFingerprint? cached, bool forceRefresh) =>
        !forceRefresh && current.Identity is not null && cached is not null
        && current == cached.Generation && cached.Contract == Contract
        && cached.Sha256.Length == 64 && cached.Sha256.All(Uri.IsHexDigit);
}

public sealed record ExactFileScopeEntry(int Id, string Path, string RelativePath, long SizeBytes, long ModifiedUtcTicks,
    int LibraryId = 0, string? Root = null);

public static class ExactDuplicateScope
{
    public static string Signature(IEnumerable<ExactFileScopeEntry> files) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(files.OrderBy(f => f.Id))));
}
