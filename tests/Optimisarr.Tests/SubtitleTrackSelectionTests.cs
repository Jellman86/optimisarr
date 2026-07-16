using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class SubtitleTrackSelectionTests
{
    [Fact]
    public void Empty_keep_list_removes_nothing()
    {
        var removals = SubtitleTrackSelection.SelectRemovals(
            new string?[] { "eng", "fra" }, Array.Empty<string>());
        Assert.Empty(removals);
    }

    [Fact]
    public void Removes_known_tracks_not_in_the_kept_languages()
    {
        var removals = SubtitleTrackSelection.SelectRemovals(
            new string?[] { "eng", "fra", "deu" }, new[] { "eng" });
        Assert.Equal(new[] { 1, 2 }, removals);
    }

    [Fact]
    public void Unknown_untagged_and_private_use_tracks_are_never_removed()
    {
        var removals = SubtitleTrackSelection.SelectRemovals(
            new string?[] { "und", null, "zxx", "mul", "qaa", "fra" }, new[] { "eng" });
        Assert.Equal(new[] { 5 }, removals);
    }

    [Fact]
    public void All_foreign_subtitles_are_removed_even_when_nothing_matches()
    {
        // Unlike audio there is no keep-at-least-one guard: subtitles are optional
        // streams, so a file can legitimately end with zero subtitle tracks.
        var removals = SubtitleTrackSelection.SelectRemovals(
            new string?[] { "fra", "deu" }, new[] { "eng" });
        Assert.Equal(new[] { 0, 1 }, removals);
    }

    [Fact]
    public void Bibliographic_and_terminology_spellings_match()
    {
        var removals = SubtitleTrackSelection.SelectRemovals(
            new string?[] { "ger", "fre" }, new[] { "deu" });
        Assert.Equal(new[] { 1 }, removals);
    }

    [Fact]
    public void Two_letter_codes_match_their_three_letter_equivalents()
    {
        var removals = SubtitleTrackSelection.SelectRemovals(
            new string?[] { "en", "fr" }, new[] { "eng" });
        Assert.Equal(new[] { 1 }, removals);
    }
}
