using Optimisarr.Api.Queue;
using Optimisarr.Core.Domain;

namespace Optimisarr.Tests;

public sealed class ActivityWatcherRequestParserTests
{
    [Fact]
    public void Parses_a_valid_request_and_trims_fields()
    {
        var request = new SaveActivityWatcherRequest(" Living room Plex ", "plex", " http://10.0.0.2:32400 ", " tok ", Enabled: true);

        Assert.True(ActivityWatcherRequestParser.TryParse(request, out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal("Living room Plex", parsed.Name);
        Assert.Equal(ActivityWatcherType.Plex, parsed.Type);
        Assert.Equal("http://10.0.0.2:32400", parsed.BaseUrl);
        Assert.Equal("tok", parsed.ApiToken);
        Assert.True(parsed.Enabled);
    }

    [Fact]
    public void A_blank_token_parses_as_null_so_updates_can_keep_the_existing_secret()
    {
        var request = new SaveActivityWatcherRequest("Jellyfin", "Jellyfin", "https://jf.example.com", "  ", Enabled: null);

        Assert.True(ActivityWatcherRequestParser.TryParse(request, out var parsed, out _));
        Assert.Null(parsed.ApiToken);
        Assert.True(parsed.Enabled);   // defaults to enabled
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Name_is_required(string? name)
    {
        var request = new SaveActivityWatcherRequest(name, "Emby", "http://h:8096", "k", true);

        Assert.False(ActivityWatcherRequestParser.TryParse(request, out _, out var error));
        Assert.Contains("Name", error);
    }

    [Fact]
    public void Rejects_an_unknown_type()
    {
        var request = new SaveActivityWatcherRequest("X", "Kodi", "http://h:8080", "k", true);

        Assert.False(ActivityWatcherRequestParser.TryParse(request, out _, out var error));
        Assert.Contains("Type", error);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://host/path")]
    [InlineData("")]
    public void Rejects_a_non_http_base_url(string url)
    {
        var request = new SaveActivityWatcherRequest("X", "Plex", url, "k", true);

        Assert.False(ActivityWatcherRequestParser.TryParse(request, out _, out var error));
        Assert.Contains("Base URL", error);
    }
}
