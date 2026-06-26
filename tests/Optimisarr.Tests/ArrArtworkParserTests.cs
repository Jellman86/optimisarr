using Optimisarr.Core.Activity;

namespace Optimisarr.Tests;

public sealed class ArrArtworkParserTests
{
    // Shape confirmed against a real Radarr /api/v3/movie response (trimmed to the fields used).
    private const string RadarrJson = """
    [
      { "title": "Godzilla x Kong: The New Empire", "year": 2024, "images": [
        { "coverType": "fanart", "remoteUrl": "https://image.tmdb.org/t/p/original/fan.jpg" },
        { "coverType": "poster", "remoteUrl": "https://image.tmdb.org/t/p/original/gxk2024.jpg", "url": "/MediaCover/15/poster.jpg" }
      ] },
      { "title": "Godzilla", "year": 2014, "images": [
        { "coverType": "poster", "remoteUrl": "https://image.tmdb.org/t/p/original/godzilla2014.jpg" }
      ] }
    ]
    """;

    [Fact]
    public void Radarr_returns_the_poster_remote_url_for_a_title_and_year_match()
    {
        // Punctuation/spacing differ from the parsed file title — the match must normalise both.
        Assert.Equal("https://image.tmdb.org/t/p/original/gxk2024.jpg",
            ArrArtworkParser.PosterRemoteUrl(RadarrJson, "Godzilla x Kong - The New Empire", 2024));
    }

    [Fact]
    public void Radarr_disambiguates_same_title_by_year()
    {
        var json = """
        [
          { "title": "Godzilla", "year": 1998, "images": [ { "coverType": "poster", "remoteUrl": "old.jpg" } ] },
          { "title": "Godzilla", "year": 2014, "images": [ { "coverType": "poster", "remoteUrl": "new.jpg" } ] }
        ]
        """;
        Assert.Equal("new.jpg", ArrArtworkParser.PosterRemoteUrl(json, "Godzilla", 2014));
    }

    [Fact]
    public void Falls_back_to_a_title_match_when_year_is_unknown()
    {
        Assert.Equal("https://image.tmdb.org/t/p/original/godzilla2014.jpg",
            ArrArtworkParser.PosterRemoteUrl(RadarrJson, "Godzilla", null));
    }

    [Fact]
    public void Sonarr_returns_the_series_poster_remote_url()
    {
        var json = """
        [
          { "title": "Breaking Bad", "year": 2008, "images": [
            { "coverType": "banner", "remoteUrl": "banner.jpg" },
            { "coverType": "poster", "remoteUrl": "https://artworks.thetvdb.com/bb-poster.jpg" }
          ] }
        ]
        """;
        Assert.Equal("https://artworks.thetvdb.com/bb-poster.jpg",
            ArrArtworkParser.PosterRemoteUrl(json, "Breaking Bad", 2008));
    }

    [Fact]
    public void Returns_null_when_the_match_has_no_poster_image()
    {
        var json = """[ { "title": "Godzilla", "year": 2014, "images": [ { "coverType": "fanart", "remoteUrl": "f.jpg" } ] } ]""";
        Assert.Null(ArrArtworkParser.PosterRemoteUrl(json, "Godzilla", 2014));
    }

    [Fact]
    public void Returns_null_when_nothing_matches_the_title()
    {
        Assert.Null(ArrArtworkParser.PosterRemoteUrl(RadarrJson, "A Different Film", 2024));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Returns_null_for_bad_or_empty_payloads(string json)
    {
        Assert.Null(ArrArtworkParser.PosterRemoteUrl(json, "Godzilla", 2014));
    }

    [Fact]
    public void Returns_null_when_the_title_is_missing()
    {
        Assert.Null(ArrArtworkParser.PosterRemoteUrl(RadarrJson, null, 2024));
    }
}
