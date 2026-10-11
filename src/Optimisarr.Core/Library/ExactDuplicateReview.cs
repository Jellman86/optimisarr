using System.Security.Cryptography;
using System.Text.Json;

namespace Optimisarr.Core.Library;

public sealed record ExactReviewScope(int Id, string Root);

/// <summary>Review preferences bind to selected roots and observed generations, never removal authority.</summary>
public static class ExactDuplicateReview
{
    public static string ScopeSignature(IEnumerable<ExactReviewScope> scope) => Hash(scope.OrderBy(s => s.Id));
    public static string Generation(int libraryId, ExactFileFingerprint fingerprint) => Hash(new
    {
        libraryId, fingerprint.Generation, fingerprint.Sha256, fingerprint.Contract
    });

    public static string? Signature(IEnumerable<ExactReviewScope> scope, ExactDuplicateGroup group)
    {
        var members = group.Copies.SelectMany(c => new[] { new ExactDuplicateAlias(c.Id, c.LibraryId, c.LibraryName, c.RelativePath, c.GenerationKey) }
            .Concat(c.Aliases ?? [])).OrderBy(c => c.Id).ToArray();
        if (members.Any(c => c.GenerationKey is null)) return null;
        return Hash(new { Contract = "exact-review-v1", Scope = scope.OrderBy(s => s.Id),
            group.Sha256, group.SizeBytes, Members = members.Select(c => new { c.Id, c.LibraryId, c.GenerationKey }) });
    }

    public static bool ValidSelection(ExactDuplicateGroup group, string choice, IReadOnlyList<int> keepIds) => choice switch
    {
        "Review" or "Ignore" => keepIds.Count == 0,
        "Keep" => keepIds.Count > 0 && keepIds.Count == keepIds.Distinct().Count()
            && keepIds.All(id => group.Copies.Any(c => c.Id == id)),
        _ => false
    };

    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value))).ToLowerInvariant();
}
