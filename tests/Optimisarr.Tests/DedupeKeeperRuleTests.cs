using Optimisarr.Core.Deduplication;

namespace Optimisarr.Tests;

public sealed class DedupeKeeperRuleTests
{
    private static DedupeCopy Copy(int id, string path, bool tracked = false, bool possibly = false, int? links = 1, string type = "Film") =>
        new(id, path, type, links, tracked, possibly);

    [Fact]
    public void The_copy_a_manager_tracks_stays_and_the_spare_moves()
    {
        var decision = DedupeKeeperRule.Decide([Copy(1, "A/Deep/Down/Film.mkv", tracked: true), Copy(2, "Film.mkv")], managersAnswered: true);

        Assert.Equal([1], decision.KeepIds);
        Assert.Equal([2], decision.SpareIds);
        Assert.Null(decision.Refusal);
    }

    [Fact]
    public void A_copy_that_only_might_be_tracked_can_move_when_another_clearly_is()
    {
        var decision = DedupeKeeperRule.Decide([Copy(1, "A/Film.mkv", tracked: true), Copy(2, "B/Film.mkv", possibly: true)], managersAnswered: true);

        Assert.Equal([2], decision.SpareIds);
    }

    [Fact]
    public void Copies_that_only_might_be_tracked_are_left_alone() =>
        Assert.Equal(DedupeCleanupRefusal.PossiblyTracked,
            DedupeKeeperRule.Decide([Copy(1, "A/Film.mkv", possibly: true), Copy(2, "B/Film.mkv", possibly: true)], managersAnswered: true).Refusal);

    [Fact]
    public void Two_tracked_copies_both_stay_and_a_third_can_still_move()
    {
        var decision = DedupeKeeperRule.Decide(
            [Copy(1, "A/Film.mkv", tracked: true), Copy(2, "B/Film.mkv", tracked: true), Copy(3, "C/Film.mkv")], managersAnswered: true);

        Assert.Equal([1, 2], decision.KeepIds);
        Assert.Equal([3], decision.SpareIds);
    }

    [Fact]
    public void When_every_copy_is_tracked_nothing_moves() =>
        Assert.Equal(DedupeCleanupRefusal.NothingToMove,
            DedupeKeeperRule.Decide([Copy(1, "A/Film.mkv", tracked: true), Copy(2, "B/Film.mkv", tracked: true)], managersAnswered: true).Refusal);

    [Fact]
    public void A_manager_that_could_not_answer_leaves_every_group_alone() =>
        Assert.Equal(DedupeCleanupRefusal.ManagerUnavailable,
            DedupeKeeperRule.Decide([Copy(1, "A/Film.mkv"), Copy(2, "B/Film.mkv")], managersAnswered: false).Refusal);

    [Fact]
    public void A_hard_linked_copy_stays_because_moving_it_frees_nothing()
    {
        var decision = DedupeKeeperRule.Decide([Copy(1, "Film.mkv"), Copy(2, "Deep/Film.mkv", links: 2)], managersAnswered: true);

        Assert.Equal([2], decision.KeepIds);
        Assert.Equal([1], decision.SpareIds);
    }

    [Theory]
    [InlineData("Music")]
    [InlineData("Photo")]
    [InlineData("Other")]
    public void Only_film_and_tv_are_cleaned_automatically(string type) =>
        Assert.Equal(DedupeCleanupRefusal.LibraryType,
            DedupeKeeperRule.Decide([Copy(1, "A/x.flac", type: type), Copy(2, "B/x.flac", type: type)], managersAnswered: true).Refusal);

    [Fact]
    public void Different_names_may_be_different_items_and_are_left_alone() =>
        Assert.Equal(DedupeCleanupRefusal.NamesDiffer,
            DedupeKeeperRule.Decide([Copy(1, "A/Film One.mkv"), Copy(2, "B/Film Two.mkv")], managersAnswered: true).Refusal);

    [Fact]
    public void The_shallowest_then_shortest_path_is_kept_when_nothing_else_decides()
    {
        Assert.Equal([2], DedupeKeeperRule.Decide([Copy(1, "A/B/Film.mkv"), Copy(2, "Longer folder name/Film.mkv")], true).KeepIds);
        Assert.Equal([2], DedupeKeeperRule.Decide([Copy(1, "Longer/Film.mkv"), Copy(2, "A/Film.mkv")], true).KeepIds);
        Assert.Equal([1], DedupeKeeperRule.Decide([Copy(1, "A/Film.mkv"), Copy(2, "B/Film.mkv")], true).KeepIds);
    }

    [Fact]
    public void A_saved_choice_to_ignore_is_honoured() =>
        Assert.Equal(DedupeCleanupRefusal.Ignored,
            DedupeKeeperRule.Decide([Copy(1, "A/Film.mkv"), Copy(2, "B/Film.mkv")], true, DedupeKeeperRule.IgnoreChoice).Refusal);

    [Fact]
    public void A_saved_choice_of_keeper_is_honoured_even_across_names()
    {
        var decision = DedupeKeeperRule.Decide([Copy(1, "A/One.mkv"), Copy(2, "Two.mkv")], true, DedupeKeeperRule.KeepChoice, [1]);

        Assert.Equal([1], decision.KeepIds);
        Assert.Equal([2], decision.SpareIds);
    }

    [Fact]
    public void A_saved_keeper_never_lets_a_tracked_copy_move()
    {
        var decision = DedupeKeeperRule.Decide([Copy(1, "A/Film.mkv"), Copy(2, "B/Film.mkv", tracked: true)], true, DedupeKeeperRule.KeepChoice, [1]);

        Assert.Equal(DedupeCleanupRefusal.NothingToMove, decision.Refusal);
    }
}
