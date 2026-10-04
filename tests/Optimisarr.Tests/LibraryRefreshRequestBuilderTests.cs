using System.Text.Json.Nodes;
using Optimisarr.Core.Activity;
using Optimisarr.Core.Domain;

namespace Optimisarr.Tests;

public sealed class LibraryRefreshRequestBuilderTests
{
    [Fact]
    public void Plex_refreshes_all_sections_with_its_token()
    {
        var request = LibraryRefreshRequestBuilder.Build(
            ActivityWatcherType.Plex, "http://plex:32400/", "tok", "/data/Movies/Heat/Heat.mkv");

        Assert.Equal("GET", request.Method);
        Assert.Equal("http://plex:32400/library/sections/all/refresh", request.Url);
        Assert.Equal("tok", request.Headers["X-Plex-Token"]);
        Assert.Null(request.JsonBody);
    }

    [Fact]
    public void Jellyfin_reports_the_changed_folder_as_modified()
    {
        var folder = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "data", "Movies", "Heat");
        var request = LibraryRefreshRequestBuilder.Build(
            ActivityWatcherType.Jellyfin, "http://jf:8096", "key", Path.Combine(folder, "Heat.mkv"));

        Assert.Equal("POST", request.Method);
        Assert.Equal("http://jf:8096/Library/Media/Updated", request.Url);
        Assert.Equal("MediaBrowser Token=\"key\"", request.Headers["Authorization"]);
        Assert.Equal("key", request.Headers["X-Emby-Token"]);
        Assert.NotNull(request.JsonBody);
        var updates = JsonNode.Parse(request.JsonBody)!["Updates"]!.AsArray();
        var update = Assert.Single(updates)!;
        Assert.Equal(folder, update["Path"]!.GetValue<string>());
        Assert.Equal("Modified", update["UpdateType"]!.GetValue<string>());
    }

    [Fact]
    public void Emby_uses_the_same_media_updated_endpoint()
    {
        var folder = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "data", "TV", "Show");
        var request = LibraryRefreshRequestBuilder.Build(
            ActivityWatcherType.Emby, "http://emby:8096/", "k", Path.Combine(folder, "S01E01.mkv"));

        Assert.Equal("POST", request.Method);
        Assert.Equal("http://emby:8096/Library/Media/Updated", request.Url);
        var update = Assert.Single(JsonNode.Parse(request.JsonBody!)!["Updates"]!.AsArray())!;
        Assert.Equal(folder, update["Path"]!.GetValue<string>());
        Assert.Equal("Modified", update["UpdateType"]!.GetValue<string>());
    }

    [Fact]
    public void A_null_token_does_not_throw_and_yields_an_empty_token()
    {
        var request = LibraryRefreshRequestBuilder.Build(
            ActivityWatcherType.Plex, "http://plex:32400", null, "/data/x.mkv");

        Assert.Equal(string.Empty, request.Headers["X-Plex-Token"]);
    }
}
