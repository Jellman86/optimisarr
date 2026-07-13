using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public class AudioTrackSelectionTests
{
    [Fact]
    public void Removes_nothing_when_no_languages_are_kept()
    {
        var removals = AudioTrackSelection.SelectRemovals(
            new string?[] { "eng", "fra" }, Array.Empty<string>());

        Assert.Empty(removals);
    }

    [Fact]
    public void Removes_tracks_whose_language_is_not_kept()
    {
        var removals = AudioTrackSelection.SelectRemovals(
            new string?[] { "fra", "eng", "spa" }, new[] { "eng" });

        Assert.Equal(new[] { 0, 2 }, removals);
    }

    [Fact]
    public void Keeps_every_track_matching_any_kept_language()
    {
        var removals = AudioTrackSelection.SelectRemovals(
            new string?[] { "jpn", "eng", "kor" }, new[] { "eng", "jpn" });

        Assert.Equal(new[] { 2 }, removals);
    }

    [Fact]
    public void Removes_nothing_when_no_track_matches_a_kept_language()
    {
        // "When in doubt, do nothing": stripping by exclusion when nothing matches would
        // either empty the file or guess at the operator's intent.
        var removals = AudioTrackSelection.SelectRemovals(
            new string?[] { "fra", "spa" }, new[] { "eng" });

        Assert.Empty(removals);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("und")]
    [InlineData("zxx")]
    [InlineData("mul")]
    public void Never_removes_a_track_whose_language_is_unknown(string? tag)
    {
        var removals = AudioTrackSelection.SelectRemovals(
            new[] { "eng", tag }, new[] { "eng" });

        Assert.Empty(removals);
    }

    [Fact]
    public void Removes_nothing_when_every_track_language_is_unknown()
    {
        var removals = AudioTrackSelection.SelectRemovals(
            new string?[] { "und", null }, new[] { "eng" });

        Assert.Empty(removals);
    }

    [Theory]
    [InlineData("ENG", "eng")]
    [InlineData("eng", " ENG ")]
    [InlineData("eng", "en")]
    [InlineData("deu", "ger")]
    [InlineData("ger", "deu")]
    [InlineData("ger", "de")]
    [InlineData("fre", "fra")]
    [InlineData("fra", "fr")]
    [InlineData("chi", "zho")]
    [InlineData("jpn", "ja")]
    public void Matches_equivalent_iso_639_forms(string trackTag, string keptCode)
    {
        // Containers tag with ISO 639-2 (often the bibliographic form, e.g. "ger"),
        // while operators may type the two-letter or terminological form.
        var removals = AudioTrackSelection.SelectRemovals(
            new string?[] { trackTag, "kor" }, new[] { keptCode });

        Assert.Equal(new[] { 1 }, removals);
    }

    [Fact]
    public void Unrecognised_kept_codes_match_only_their_exact_tag()
    {
        var removals = AudioTrackSelection.SelectRemovals(
            new string?[] { "tlh", "eng" }, new[] { "tlh" });

        Assert.Equal(new[] { 1 }, removals);
    }

    [Fact]
    public void Parses_a_stored_comma_separated_list()
    {
        Assert.Equal(new[] { "eng", "jpn" }, AudioTrackSelection.ParseLanguageList("eng, jpn"));
        Assert.Equal(new[] { "eng" }, AudioTrackSelection.ParseLanguageList(" ENG ,, eng "));
        Assert.Empty(AudioTrackSelection.ParseLanguageList(null));
        Assert.Empty(AudioTrackSelection.ParseLanguageList("   "));
    }

    [Fact]
    public void Parses_stored_track_languages_preserving_order_and_position()
    {
        // Unlike the kept-languages list, track entries are positional (index = audio-relative
        // stream index), so nothing may be deduplicated or dropped.
        Assert.Equal(
            new string?[] { "eng", "eng", "und" },
            AudioTrackSelection.ParseTrackLanguages("eng, eng, und"));
        Assert.Equal(new string?[] { null, "eng" }, AudioTrackSelection.ParseTrackLanguages(", eng"));
        Assert.Null(AudioTrackSelection.ParseTrackLanguages(null));
        Assert.Null(AudioTrackSelection.ParseTrackLanguages("   "));
    }
}
