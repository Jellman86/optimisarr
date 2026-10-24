using Optimisarr.Core.Library;

namespace Optimisarr.Tests;

public sealed class ExactDuplicateCacheTests
{
    private static readonly ExactFileGeneration Generation = new("/media", "/media/a", 3, 123, "device:inode");
    private static readonly Optimisarr.Core.Library.ExactFileFingerprint Fingerprint = new(Generation, new string('a', 64),
        DateTimeOffset.Parse("2026-10-01T00:00:00Z"), ExactDuplicateCache.Contract);

    [Fact]
    public void Unchanged_generation_reuses_the_original_content_read_time()
    {
        Assert.True(ExactDuplicateCache.CanReuse(Generation, Fingerprint, false));
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T00:00:00Z"), Fingerprint.CheckedAt);
    }

    [Fact]
    public void Refresh_unknown_identity_and_incomplete_or_old_contract_never_reuse_hashes()
    {
        Assert.False(ExactDuplicateCache.CanReuse(Generation, Fingerprint, true));
        Assert.False(ExactDuplicateCache.CanReuse(Generation with { Identity = null }, Fingerprint, false));
        Assert.False(ExactDuplicateCache.CanReuse(Generation, Fingerprint with { Sha256 = "partial" }, false));
        Assert.False(ExactDuplicateCache.CanReuse(Generation, Fingerprint with { Contract = "old" }, false));
        Assert.False(ExactDuplicateCache.CanReuse(Generation, null, false));
    }

    [Fact]
    public void Every_generation_component_invalidates_cached_evidence()
    {
        ExactFileGeneration[] changed = [Generation with { Root = "/other" }, Generation with { Path = "/media/b" },
            Generation with { SizeBytes = 4 }, Generation with { ModifiedUtcTicks = 124 },
            Generation with { Identity = "device:replacement" }];
        Assert.All(changed, generation => Assert.False(ExactDuplicateCache.CanReuse(generation, Fingerprint, false)));
    }
    [Fact]
    public void Scope_signature_detects_addition_replacement_and_metadata_changes_independent_of_order()
    {
        var a = new ExactFileScopeEntry(1, "/a", "a", 3, 123);
        var b = new ExactFileScopeEntry(2, "/b", "b", 3, 123);
        var first = ExactDuplicateScope.Signature([a, b]);
        Assert.Equal(first, ExactDuplicateScope.Signature([b, a]));
        Assert.NotEqual(first, ExactDuplicateScope.Signature([a]));
        Assert.NotEqual(first, ExactDuplicateScope.Signature([a, b with { ModifiedUtcTicks = 124 }]));
        Assert.NotEqual(first, ExactDuplicateScope.Signature([a, b with { Id = 3 }]));
    }

    [Fact]
    public void Scope_signature_handles_a_100000_entry_inventory_deterministically()
    {
        var entries = Enumerable.Range(1, 100_000).Select(i => new ExactFileScopeEntry(i,
            $"/media/{i:D6}.mkv", $"{i:D6}.mkv", i, i)).ToArray();
        Assert.Equal(ExactDuplicateScope.Signature(entries), ExactDuplicateScope.Signature(entries.Reverse()));
    }
}
