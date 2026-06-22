using Optimisarr.Core.Activity;

namespace Optimisarr.Tests;

public sealed class ArtworkSearchParserTests
{
    // Shape confirmed against a real Plex /search response.
    private const string PlexJson = """
    { "MediaContainer": { "size": 2, "Metadata": [
      { "type": "movie", "title": "After Yang", "year": 2022, "art": "/library/metadata/4492/art/1782109387", "thumb": "/library/metadata/4492/thumb/1" },
      { "type": "movie", "title": "After Yang (short)", "year": 2015, "art": "/library/metadata/9/art/2" }
    ] } }
    """;

    [Fact]
    public void Plex_returns_the_matching_year_backdrop()
    {
        Assert.Equal("/library/metadata/4492/art/1782109387",
            ArtworkSearchParser.PlexArtPath(PlexJson, isTv: false, year: 2022));
    }

    [Fact]
    public void Plex_falls_back_to_first_typed_match_when_year_unknown()
    {
        Assert.Equal("/library/metadata/4492/art/1782109387",
            ArtworkSearchParser.PlexArtPath(PlexJson, isTv: false, year: null));
    }

    [Fact]
    public void Plex_skips_tv_kinds_when_a_movie_is_wanted_but_falls_back_to_any_art()
    {
        var json = """{ "MediaContainer": { "Metadata": [ { "type": "show", "art": "/show/art" } ] } }""";
        // No movie match, but there is art — better a show backdrop than nothing.
        Assert.Equal("/show/art", ArtworkSearchParser.PlexArtPath(json, isTv: false, year: null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    public void Plex_returns_null_for_bad_payloads(string json)
    {
        Assert.Null(ArtworkSearchParser.PlexArtPath(json, isTv: false, year: null));
    }

    [Fact]
    public void Jellyfin_builds_a_backdrop_url_for_the_matching_item()
    {
        var json = """
        { "Items": [
          { "Id": "abc", "Type": "Movie", "ProductionYear": 2022, "BackdropImageTags": ["tag123"] }
        ] }
        """;
        Assert.Equal("/Items/abc/Images/Backdrop/0?tag=tag123",
            ArtworkSearchParser.JellyfinBackdropPath(json, isTv: false, year: 2022));
    }

    [Fact]
    public void Jellyfin_returns_null_when_no_item_has_a_backdrop()
    {
        var json = """{ "Items": [ { "Id": "x", "Type": "Movie", "BackdropImageTags": [] } ] }""";
        Assert.Null(ArtworkSearchParser.JellyfinBackdropPath(json, isTv: false, year: null));
    }
}
