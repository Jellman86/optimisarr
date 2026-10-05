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
            ArtworkSearchParser.PlexArtPath(PlexJson, "After Yang", isTv: false, year: 2022));
    }

    [Fact]
    public void Plex_matches_the_title_alone_when_the_year_is_unknown()
    {
        Assert.Equal("/library/metadata/4492/art/1782109387",
            ArtworkSearchParser.PlexArtPath(PlexJson, "After Yang", isTv: false, year: null));
    }

    // Plex search is fuzzy: "The Odyssey" also finds "Homer's Odyssey", an episode of The Simpsons.
    // Showing that art for the film would tell the operator they are looking at a different title.
    [Fact]
    public void Plex_never_returns_another_titles_art()
    {
        var json = """
        { "MediaContainer": { "Hub": [ { "type": "episode", "Metadata": [
          { "type": "episode", "title": "Homer's Odyssey", "grandparentTitle": "The Simpsons", "art": "/library/metadata/77/art/1", "thumb": "/library/metadata/78/thumb/1" }
        ] } ] } }
        """;
        Assert.Null(ArtworkSearchParser.PlexArtPath(json, "The Odyssey", isTv: false, year: 2026));
        Assert.Null(ArtworkSearchParser.PlexPosterPath(json, "The Odyssey", isTv: false, year: 2026));
    }

    [Fact]
    public void Plex_skips_a_show_when_a_film_of_the_same_name_is_wanted()
    {
        var json = """{ "MediaContainer": { "Metadata": [ { "type": "show", "title": "Fargo", "art": "/show/art" } ] } }""";
        Assert.Null(ArtworkSearchParser.PlexArtPath(json, "Fargo", isTv: false, year: 1996));
    }

    [Fact]
    public void Plex_rejects_a_remake_from_another_year_but_tolerates_a_year_either_side()
    {
        var json = """
        { "MediaContainer": { "Metadata": [
          { "type": "movie", "title": "Dune", "year": 1984, "art": "/dune/1984" },
          { "type": "movie", "title": "Dune", "year": 2021, "art": "/dune/2021" }
        ] } }
        """;
        Assert.Equal("/dune/2021", ArtworkSearchParser.PlexArtPath(json, "Dune", isTv: false, year: 2020));
        Assert.Null(ArtworkSearchParser.PlexArtPath(json, "Dune", isTv: false, year: 2000));
    }

    [Fact]
    public void Plex_finds_a_show_through_its_episodes_and_seasons()
    {
        var json = """
        { "MediaContainer": { "Hub": [ { "type": "episode", "Metadata": [
          { "type": "episode", "title": "Shaun", "grandparentTitle": "Bluey", "year": 2019, "art": "/bluey/art", "grandparentThumb": "/bluey/thumb", "thumb": "/bluey/s1e50/thumb" }
        ] } ] } }
        """;
        Assert.Equal("/bluey/art", ArtworkSearchParser.PlexArtPath(json, "Bluey", isTv: true, year: 2018));
        // An episode's own thumb is a still; the show's poster is its grandparent thumb.
        Assert.Equal("/bluey/thumb", ArtworkSearchParser.PlexPosterPath(json, "Bluey", isTv: true, year: 2018));
    }

    [Theory]
    [InlineData("Marvel's Agents of S H I E L D", "Marvel’s Agents of S.H.I.E.L.D.")]
    [InlineData("Bluey", "Bluey (2018)")]
    [InlineData("Pokemon", "Pokémon")]
    [InlineData("Law and Order", "Law & Order")]
    public void Titles_match_across_punctuation_accents_and_a_year_suffix(string wanted, string listed)
    {
        var json = $$"""{ "MediaContainer": { "Metadata": [ { "type": "show", "title": "{{listed}}", "art": "/art" } ] } }""";
        Assert.Equal("/art", ArtworkSearchParser.PlexArtPath(json, wanted, isTv: true, year: null));
    }

    // Shape confirmed against a real Plex /hubs/search response: results are grouped under
    // MediaContainer.Hub[].Metadata rather than a top-level Metadata array. (Plain /search on the
    // same server returns only providers, so the parser must handle the hubs shape.)
    private const string PlexHubsJson = """
    { "MediaContainer": { "size": 2, "Hub": [
      { "type": "tag", "Metadata": [] },
      { "type": "movie", "Metadata": [
        { "type": "movie", "title": "Jurassic World: Dominion", "year": 2022, "art": "/library/metadata/3117/art/1781507082" }
      ] }
    ] } }
    """;

    [Fact]
    public void Plex_reads_results_from_the_hubs_search_shape()
    {
        Assert.Equal("/library/metadata/3117/art/1781507082",
            ArtworkSearchParser.PlexArtPath(PlexHubsJson, "Jurassic World Dominion", isTv: false, year: 2022));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    public void Plex_returns_null_for_bad_payloads(string json)
    {
        Assert.Null(ArtworkSearchParser.PlexArtPath(json, "After Yang", isTv: false, year: null));
    }

    [Fact]
    public void Plex_returns_null_when_search_yields_only_providers()
    {
        // A /search response with no Metadata and no Hub results — must not throw, must yield null.
        var json = """{ "MediaContainer": { "size": 4, "Provider": [ { "key": "/system/search" } ] } }""";
        Assert.Null(ArtworkSearchParser.PlexArtPath(json, "After Yang", isTv: false, year: null));
    }

    [Fact]
    public void Jellyfin_builds_a_backdrop_url_for_the_matching_item()
    {
        var json = """
        { "Items": [
          { "Id": "abc", "Name": "After Yang", "Type": "Movie", "ProductionYear": 2022, "BackdropImageTags": ["tag123"] }
        ] }
        """;
        Assert.Equal("/Items/abc/Images/Backdrop/0?tag=tag123",
            ArtworkSearchParser.JellyfinBackdropPath(json, "After Yang", isTv: false, year: 2022));
    }

    [Fact]
    public void Jellyfin_never_returns_another_titles_artwork()
    {
        var json = """
        { "Items": [
          { "Id": "abc", "Name": "The Simpsons", "Type": "Series", "BackdropImageTags": ["b"], "ImageTags": { "Primary": "p" } }
        ] }
        """;
        Assert.Null(ArtworkSearchParser.JellyfinBackdropPath(json, "The Odyssey", isTv: false, year: 2026));
        Assert.Null(ArtworkSearchParser.JellyfinPosterPath(json, "The Odyssey", isTv: false, year: 2026));
    }

    [Fact]
    public void Jellyfin_returns_null_when_no_item_has_a_backdrop()
    {
        var json = """{ "Items": [ { "Id": "x", "Name": "After Yang", "Type": "Movie", "BackdropImageTags": [] } ] }""";
        Assert.Null(ArtworkSearchParser.JellyfinBackdropPath(json, "After Yang", isTv: false, year: null));
    }

    [Fact]
    public void Plex_returns_the_matching_year_poster_thumb()
    {
        Assert.Equal("/library/metadata/4492/thumb/1",
            ArtworkSearchParser.PlexPosterPath(PlexJson, "After Yang", isTv: false, year: 2022));
    }

    [Fact]
    public void Jellyfin_builds_a_poster_url_from_the_primary_image_tag()
    {
        var json = """
        { "Items": [
          { "Id": "abc", "Name": "After Yang", "Type": "Movie", "ProductionYear": 2022, "ImageTags": { "Primary": "ptag" } }
        ] }
        """;
        Assert.Equal("/Items/abc/Images/Primary?tag=ptag",
            ArtworkSearchParser.JellyfinPosterPath(json, "After Yang", isTv: false, year: 2022));
    }

    [Fact]
    public void Jellyfin_returns_null_when_no_item_has_a_primary_image()
    {
        var json = """{ "Items": [ { "Id": "x", "Name": "After Yang", "Type": "Movie", "ImageTags": {} } ] }""";
        Assert.Null(ArtworkSearchParser.JellyfinPosterPath(json, "After Yang", isTv: false, year: null));
    }
}
