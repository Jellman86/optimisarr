using Optimisarr.Core.Activity;
using Optimisarr.Api.Queue;

namespace Optimisarr.Tests;

public sealed class PlaybackArtworkTests
{
    [Theory]
    [InlineData("movie", "thumb", "/library/metadata/1/thumb/2")]
    [InlineData("episode", "grandparentThumb", "/library/metadata/3/thumb/4")]
    [InlineData("track", "parentThumb", "/library/metadata/5/thumb/6")]
    public void Plex_uses_the_reported_film_show_or_album_art(string kind, string attribute, string path)
    {
        var fallback = kind == "movie" ? "" : "thumb=\"/library/metadata/9/thumb/10\"";
        var session = Assert.Single(PlexSessionsParser.ParseSessions($"<MediaContainer><Video type=\"{kind}\" {attribute}=\"{path}\" {fallback} /></MediaContainer>"));
        Assert.Equal(path, session.Artwork?.Path);
    }

    [Theory]
    [InlineData("https://attacker.invalid/image")]
    [InlineData("//attacker.invalid/image")]
    [InlineData("/library/metadata/1/../thumb")]
    [InlineData("/library/metadata/1/thumb?X-Plex-Token=secret")]
    [InlineData("/library/metadata/1/thumb%2f..%2fsecret")]
    public void Untrusted_Plex_paths_are_never_proxy_targets(string path)
    {
        Assert.Null(Assert.Single(PlexSessionsParser.ParseSessions($"<MediaContainer><Video type=\"movie\" thumb=\"{path}\" /></MediaContainer>")).Artwork);
    }

    [Theory]
    [InlineData("Movie", "Id", "ImageTags", "Primary")]
    [InlineData("Episode", "SeriesId", null, "SeriesPrimaryImageTag")]
    [InlineData("Audio", "AlbumId", null, "AlbumPrimaryImageTag")]
    public void Jellyfin_and_Emby_use_exact_item_show_or_album_image_identity(string kind, string idKey, string? tagsKey, string tagKey)
    {
        var tags = tagsKey is null ? $"\"{tagKey}\":\"abc123\"" : $"\"{tagsKey}\":{{\"{tagKey}\":\"abc123\"}}";
        var json = "[{\"NowPlayingItem\":{\"Type\":\"" + kind + "\",\"" + idKey + "\":\"11111111-2222-3333-4444-555555555555\"," + tags + "}}]";
        var session = Assert.Single(JellyfinSessionsParser.ParseSessions(json));
        Assert.Equal("/Items/11111111222233334444555555555555/Images/Primary?maxWidth=240&quality=85&tag=abc123", session.Artwork?.Path);
    }

    [Fact]
    public void Emby_numeric_item_ids_are_supported_without_guessing_an_image()
    {
        var session = Assert.Single(JellyfinSessionsParser.ParseSessions("""[{"NowPlayingItem":{"Type":"Audio","AlbumId":"12345","AlbumPrimaryImageTag":"abc"}}]"""));
        Assert.Equal("/Items/12345/Images/Primary?maxWidth=240&quality=85&tag=abc", session.Artwork?.Path);
    }

    [Theory]
    [InlineData("../image", "abc")]
    [InlineData("//attacker.invalid", "abc")]
    [InlineData("12345", "abc&api_key=secret")]
    [InlineData("12345", "")]
    public void MediaBrowser_rejects_paths_and_query_injection_in_image_identity(string id, string tag) =>
        Assert.Null(PlaybackArtwork.MediaBrowser(id, tag));

    [Fact]
    public void Proxy_identity_distinguishes_watchers_and_never_exposes_tokens_paths_or_viewers()
    {
        var session = Assert.Single(PlexSessionsParser.ParseSessions("""<MediaContainer><Video type="movie" thumb="/library/metadata/1/thumb/2"><User title="private viewer" /></Video></MediaContainer>"""));
        var first = PlaybackHoldDto.From(new PlaybackHold("Plex", session, 1));
        var second = PlaybackHoldDto.From(new PlaybackHold("Plex", session, 2));
        Assert.Matches("^/api/playback/[A-F0-9]{64}/artwork$", first.ArtworkUrl!);
        Assert.NotEqual(first.ArtworkUrl, second.ArtworkUrl);
        Assert.Equal(first.ArtworkUrl, PlaybackHoldDto.From(new PlaybackHold("Plex", session.WithoutViewer(), 1)).ArtworkUrl);
    }
}
