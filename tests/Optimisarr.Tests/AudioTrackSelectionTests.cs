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
    [InlineData("mis")]
    [InlineData("zxx")]
    [InlineData("mul")]
    [InlineData("qaa")]
    [InlineData("qqq")]
    [InlineData("qtz")]
    [InlineData("afa")]
    [InlineData("en-US")]
    [InlineData("not-a-language")]
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
    [InlineData("afr", "af")]
    [InlineData("mri", "mi")]
    [InlineData("mao", "mi")]
    public void Matches_equivalent_iso_639_forms(string trackTag, string keptCode)
    {
        // Containers tag with ISO 639-2 (often the bibliographic form, e.g. "ger"),
        // while operators may type the two-letter or terminological form.
        var removals = AudioTrackSelection.SelectRemovals(
            new string?[] { trackTag, "kor" }, new[] { keptCode });

        Assert.Equal(new[] { 1 }, removals);
    }

    [Fact]
    public void Recognised_iso_639_2_codes_without_a_two_letter_form_are_supported()
    {
        var removals = AudioTrackSelection.SelectRemovals(
            new string?[] { "tlh", "eng" }, new[] { "tlh" });

        Assert.Equal(new[] { 1 }, removals);
    }

    [Fact]
    public void Parses_a_stored_comma_separated_list()
    {
        Assert.Equal(new[] { "eng", "jpn" }, TrackLanguages.ParseLanguageList("eng, jpn"));
        Assert.Equal(new[] { "eng" }, TrackLanguages.ParseLanguageList(" ENG ,, eng "));
        Assert.Empty(TrackLanguages.ParseLanguageList(null));
        Assert.Empty(TrackLanguages.ParseLanguageList("   "));
    }

    [Fact]
    public void A_partly_invalid_stored_list_disables_the_entire_destructive_rule()
    {
        // A legacy/manual database value must not be weakened from "eng + something unknown"
        // into "eng only", which could authorise removal the operator never intended.
        Assert.Empty(TrackLanguages.ParseLanguageList("eng, qqq"));
        Assert.Empty(TrackLanguages.ParseLanguageList("eng, afa"));
    }

    [Fact]
    public void Normalises_a_valid_kept_language_list_for_storage()
    {
        var valid = TrackLanguages.TryNormaliseLanguageList(
            " EN, jpn, eng, fre, ace ", out var normalised);

        Assert.True(valid);
        Assert.Equal("eng, jpn, fra, ace", normalised);
    }

    [Fact]
    public void Blank_kept_language_list_normalises_to_keep_everything()
    {
        var valid = TrackLanguages.TryNormaliseLanguageList("   ", out var normalised);

        Assert.True(valid);
        Assert.Null(normalised);
    }

    [Theory]
    [InlineData("english")]
    [InlineData("en1")]
    [InlineData(",,,")]
    [InlineData("qqq")]
    [InlineData("qaa")]
    [InlineData("afa")]
    [InlineData("und")]
    public void Rejects_a_malformed_kept_language_list(string value)
    {
        Assert.False(TrackLanguages.TryNormaliseLanguageList(value, out _));
    }

    [Fact]
    public void Rejects_a_kept_language_list_that_exceeds_the_storage_limit()
    {
        var value = string.Join(",", Enumerable.Range(0, 65).Select(index =>
            $"{(char)('a' + index / 26)}{(char)('a' + index % 26)}a"));

        Assert.False(TrackLanguages.TryNormaliseLanguageList(value, out _));
    }

    [Fact]
    public void Parses_stored_track_languages_preserving_order_and_position()
    {
        // Unlike the kept-languages list, track entries are positional (index = audio-relative
        // stream index), so nothing may be deduplicated or dropped.
        Assert.Equal(
            new string?[] { "eng", "eng", "und" },
            TrackLanguages.ParseTrackLanguages("eng, eng, und"));
        Assert.Equal(new string?[] { null, "eng" }, TrackLanguages.ParseTrackLanguages(", eng"));
        Assert.Null(TrackLanguages.ParseTrackLanguages(null));
        Assert.Null(TrackLanguages.ParseTrackLanguages("   "));
    }
}
