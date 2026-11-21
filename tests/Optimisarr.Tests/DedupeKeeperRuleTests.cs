using Optimisarr.Core.Deduplication;

namespace Optimisarr.Tests;

public sealed class DedupeKeeperRuleTests
{
    private static DedupeCopy Copy(int id, string path, bool tracked = false, bool possibly = false, int? links = 1, string type = "Film", int library = 1) =>
        new(id, library, path, type, links, tracked, possibly);

    [Fact]
    public void The_copy_a_manager_tracks_stays_and_the_spare_moves()
    {
        var decision = DedupeKeeperRule.Decide([Copy(1, "A/Deep/Down/Title.mkv", tracked: true), Copy(2, "Title.mkv")], managersAnswered: true);

        Assert.Equal([1], decision.KeepIds);
        Assert.Equal([2], decision.SpareIds);
        Assert.Null(decision.Refusal);
    }

    [Fact]
    public void A_copy_that_only_might_be_tracked_can_move_when_another_clearly_is()
    {
        var decision = DedupeKeeperRule.Decide([Copy(1, "A/Title.mkv", tracked: true), Copy(2, "B/Title.mkv", possibly: true)], managersAnswered: true);

        Assert.Equal([2], decision.SpareIds);
    }

    [Fact]
    public void Copies_that_only_might_be_tracked_are_left_alone() =>
        Assert.Equal(DedupeCleanupRefusal.PossiblyTracked,
            DedupeKeeperRule.Decide([Copy(1, "A/Title.mkv", possibly: true), Copy(2, "B/Title.mkv", possibly: true)], managersAnswered: true).Refusal);

    [Fact]
    public void Two_tracked_copies_both_stay_and_a_third_can_still_move()
    {
        var decision = DedupeKeeperRule.Decide(
            [Copy(1, "A/Title.mkv", tracked: true), Copy(2, "B/Title.mkv", tracked: true), Copy(3, "C/Title.mkv")], managersAnswered: true);

        Assert.Equal([1, 2], decision.KeepIds);
        Assert.Equal([3], decision.SpareIds);
    }

    [Fact]
    public void When_every_copy_is_tracked_nothing_moves() =>
        Assert.Equal(DedupeCleanupRefusal.NothingToMove,
            DedupeKeeperRule.Decide([Copy(1, "A/Title.mkv", tracked: true), Copy(2, "B/Title.mkv", tracked: true)], managersAnswered: true).Refusal);

    [Fact]
    public void A_manager_that_could_not_answer_leaves_every_group_alone() =>
        Assert.Equal(DedupeCleanupRefusal.ManagerUnavailable,
            DedupeKeeperRule.Decide([Copy(1, "A/Title.mkv"), Copy(2, "B/Title.mkv")], managersAnswered: false).Refusal);

    [Fact]
    public void A_hard_linked_copy_stays_because_moving_it_frees_nothing()
    {
        var decision = DedupeKeeperRule.Decide([Copy(1, "Title.mkv"), Copy(2, "Deep/Title.mkv", links: 2)], managersAnswered: true);

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

    [Theory]
    [InlineData("Film One/extras/trailer.mkv", "Film Two/extras/trailer.mkv")]
    [InlineData("Film One/Featurettes/Making Of.mkv", "Film Two/Featurettes/Making Of.mkv")]
    [InlineData("Film One/Film-trailer.mkv", "Film Two/Film-trailer.mkv")]
    [InlineData("Show/intro.mkv", "Other Show/intro.mkv")]
    [InlineData("Film One/movie.mkv", "Film Two/movie.mkv")]
    public void An_extra_can_belong_to_several_titles_so_it_is_left_alone(string first, string second) =>
        Assert.Equal(DedupeCleanupRefusal.SharedExtra, DedupeKeeperRule.Decide([Copy(1, first), Copy(2, second)], true).Refusal);

    [Fact]
    public void An_episode_in_a_duplicated_season_folder_is_not_an_extra() =>
        Assert.Null(DedupeKeeperRule.Decide([Copy(1, "Show/Season 1/Show S01E01.mkv"), Copy(2, "Show/Season 01/Show S01E01.mkv")], true).Refusal);

    [Fact]
    public void The_same_film_in_two_libraries_can_be_deliberate_so_it_is_left_alone() =>
        Assert.Equal(DedupeCleanupRefusal.SeparateLibraries,
            DedupeKeeperRule.Decide([Copy(1, "Film/Title.mkv", tracked: true), Copy(2, "Film/Title.mkv", library: 2)], true).Refusal);

    [Fact]
    public void A_saved_keeper_says_placements_are_interchangeable()
    {
        var decision = DedupeKeeperRule.Decide([Copy(1, "Film One/extras/trailer.mkv"), Copy(2, "Film/x.mkv", library: 2)], true, DedupeKeeperRule.KeepChoice, [1]);

        Assert.Equal([2], decision.SpareIds);
    }

    [Fact]
    public void The_shallowest_then_shortest_path_is_kept_when_nothing_else_decides()
    {
        Assert.Equal([2], DedupeKeeperRule.Decide([Copy(1, "A/B/Title.mkv"), Copy(2, "Longer folder name/Title.mkv")], true).KeepIds);
        Assert.Equal([2], DedupeKeeperRule.Decide([Copy(1, "Longer/Title.mkv"), Copy(2, "A/Title.mkv")], true).KeepIds);
        Assert.Equal([1], DedupeKeeperRule.Decide([Copy(1, "A/Title.mkv"), Copy(2, "B/Title.mkv")], true).KeepIds);
    }

    [Fact]
    public void A_saved_choice_to_ignore_is_honoured() =>
        Assert.Equal(DedupeCleanupRefusal.Ignored,
            DedupeKeeperRule.Decide([Copy(1, "A/Title.mkv"), Copy(2, "B/Title.mkv")], true, DedupeKeeperRule.IgnoreChoice).Refusal);

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
        var decision = DedupeKeeperRule.Decide([Copy(1, "A/Title.mkv"), Copy(2, "B/Title.mkv", tracked: true)], true, DedupeKeeperRule.KeepChoice, [1]);

        Assert.Equal(DedupeCleanupRefusal.NothingToMove, decision.Refusal);
    }
}
