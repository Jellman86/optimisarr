using Optimisarr.Core.Library;

namespace Optimisarr.Tests;

public sealed class ExactDuplicateReviewTests
{
    private static ExactDuplicateFile Copy(int id, int library, string identity) =>
        new(id, "same.mkv", 42, new('a', 64), 1, DateTimeOffset.UnixEpoch,
            LibraryId: library, PhysicalKey: identity, GenerationKey: $"generation-{id}");

    [Fact]
    public void Equal_relative_paths_in_different_libraries_are_distinct_copies()
    {
        var group = Assert.Single(ExactDuplicateGrouping.Group([Copy(1, 1, "disk:1"), Copy(2, 2, "disk:2")]));
        Assert.Equal(2, group.TotalCopies);
        Assert.Equal(42, group.ExtraCopyBytes);
    }

    [Fact]
    public void Aliases_are_references_to_one_physical_copy_and_do_not_inflate_bytes()
    {
        Assert.Empty(ExactDuplicateGrouping.Group([Copy(1, 1, "disk:1"), Copy(2, 2, "disk:1")]));
        var group = Assert.Single(ExactDuplicateGrouping.Group([Copy(1, 1, "disk:1"), Copy(2, 2, "disk:1"), Copy(3, 2, "disk:3")]));
        Assert.Equal(2, group.TotalCopies);
        Assert.Single(group.Copies[0].Aliases!);
        Assert.Equal(42, group.ExtraCopyBytes);
    }

    [Fact]
    public void Any_linked_or_unknown_alias_makes_the_physical_copy_space_estimate_unknown()
    {
        var a = Copy(1, 1, "disk:1");
        foreach (int? links in new int?[] { 2, null })
        {
            var alias = Copy(2, 2, "disk:1") with { HardLinkCount = links };
            var group = Assert.Single(ExactDuplicateGrouping.Group([a, alias, Copy(3, 2, "disk:3")]));
            Assert.Null(group.ExtraCopyBytes);
        }
    }

    [Fact]
    public void Review_signature_is_order_independent_and_tied_to_scope_and_all_alias_generations()
    {
        var copies = new[] { Copy(1, 1, "disk:1"), Copy(2, 2, "disk:2") };
        ExactReviewScope[] scope = [new(1, "/a"), new(2, "/b")];
        var group = Assert.Single(ExactDuplicateGrouping.Group(copies));
        var key = ExactDuplicateReview.Signature(scope, group);
        Assert.NotNull(key);
        Assert.Equal(key, ExactDuplicateReview.Signature(scope.Reverse(), group with { Copies = group.Copies.Reverse().ToArray() }));
        Assert.NotEqual(key, ExactDuplicateReview.Signature([scope[0]], group));
        Assert.NotEqual(key, ExactDuplicateReview.Signature(scope, group with { Copies = [copies[0], copies[1] with { GenerationKey = "changed" }] }));
        Assert.Null(ExactDuplicateReview.Signature(scope, group with { Copies = [copies[0], copies[1] with { GenerationKey = null }] }));
    }

    [Fact]
    public void Keep_decisions_allow_multiple_copies_but_cannot_select_a_reference_outside_the_group()
    {
        var group = Assert.Single(ExactDuplicateGrouping.Group([Copy(1, 1, "disk:1"), Copy(2, 2, "disk:2")]));
        Assert.True(ExactDuplicateReview.ValidSelection(group, "Keep", [1, 2]));
        Assert.True(ExactDuplicateReview.ValidSelection(group, "Ignore", []));
        Assert.True(ExactDuplicateReview.ValidSelection(group, "Review", []));
        Assert.False(ExactDuplicateReview.ValidSelection(group, "Keep", []));
        Assert.False(ExactDuplicateReview.ValidSelection(group, "Keep", [3]));
        Assert.False(ExactDuplicateReview.ValidSelection(group, "Keep", [1, 1]));
        Assert.False(ExactDuplicateReview.ValidSelection(group, "Ignore", [1]));
        Assert.False(ExactDuplicateReview.ValidSelection(group, "Delete", [1]));
    }

    [Fact]
    public void Conflicting_hashes_for_one_physical_file_cannot_supply_duplicate_evidence()
    {
        var a = Copy(1, 1, "disk:1"); var b = Copy(2, 2, "disk:1") with { Sha256 = new('b',64) };
        Assert.Empty(ExactDuplicateGrouping.Group([a, b, Copy(3, 1, "disk:3")]));
    }

    [Fact]
    public void Truncating_aliases_disables_decisions_and_retains_response_budgets()
    {
        var many = Enumerable.Range(1,101).Select(i => Copy(i, 1, "disk:1"));
        var group = Assert.Single(ExactDuplicateGrouping.Group(many.Prepend(Copy(200, 0, "disk:2"))));
        var display = ExactDuplicateGrouping.Limit([group with { CanDecide = true }]);
        Assert.True(display.Truncated);
        Assert.False(Assert.Single(display.Groups).CanDecide);
        Assert.True(display.Groups.Sum(g => g.Copies.Sum(c => 1 + (c.Aliases?.Count ?? 0))) <= 100);
    }

    [Fact]
    public void Large_alias_lists_cannot_hide_the_second_physical_copy()
    {
        var aliases = Enumerable.Range(1,101).Select(i => Copy(i, 1, "disk:1"));
        var group = Assert.Single(ExactDuplicateGrouping.Group(aliases.Append(Copy(200, 2, "disk:2"))));
        var display = ExactDuplicateGrouping.Limit([group with { CanDecide = true }]);
        var visible = Assert.Single(display.Groups);
        Assert.Equal(2, visible.Copies.Count); Assert.True(display.Truncated); Assert.False(visible.CanDecide);
        Assert.Equal(100, visible.Copies.Sum(c => 1 + (c.Aliases?.Count ?? 0)));
    }

    [Fact]
    public void Inventory_scope_tracks_library_ownership_and_roots_as_well_as_file_metadata()
    {
        var entry = new ExactFileScopeEntry(1, "/root/sub/a", "a", 42, 123, 1, "/root");
        Assert.NotEqual(ExactDuplicateScope.Signature([entry]), ExactDuplicateScope.Signature([entry with { LibraryId = 2, Root = "/root/sub" }]));
    }
}
