using Optimisarr.Core.Activity;

namespace Optimisarr.Tests;

public sealed class PlexSessionsParserTests
{
    [Fact]
    public void Counts_the_session_elements()
    {
        const string xml = """
            <MediaContainer size="2">
              <Video sessionKey="1" title="A" />
              <Track sessionKey="2" title="B" />
            </MediaContainer>
            """;

        Assert.Equal(2, PlexSessionsParser.ParseActiveSessions(xml));
    }

    [Fact]
    public void An_empty_container_is_zero()
    {
        Assert.Equal(0, PlexSessionsParser.ParseActiveSessions("""<MediaContainer size="0" />"""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not xml at all")]
    public void Blank_or_invalid_xml_is_zero(string xml)
    {
        Assert.Equal(0, PlexSessionsParser.ParseActiveSessions(xml));
    }
}

public sealed class JellyfinSessionsParserTests
{
    [Fact]
    public void Counts_only_sessions_with_a_now_playing_item()
    {
        const string json = """
            [
              { "Id": "a", "NowPlayingItem": { "Name": "Movie" } },
              { "Id": "b" },
              { "Id": "c", "NowPlayingItem": null },
              { "Id": "d", "NowPlayingItem": { "Name": "Episode" } }
            ]
            """;

        Assert.Equal(2, JellyfinSessionsParser.ParseActiveSessions(json));
    }

    [Fact]
    public void An_empty_array_is_zero()
    {
        Assert.Equal(0, JellyfinSessionsParser.ParseActiveSessions("[]"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("{\"not\":\"an array\"}")]
    public void Blank_or_invalid_json_is_zero(string json)
    {
        Assert.Equal(0, JellyfinSessionsParser.ParseActiveSessions(json));
    }
}

public sealed class ActivityPauseEvaluatorTests
{
    [Fact]
    public void Not_active_when_no_watchers()
    {
        var decision = ActivityPauseEvaluator.Evaluate([]);

        Assert.False(decision.Active);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void Active_when_a_reachable_watcher_is_streaming()
    {
        var decision = ActivityPauseEvaluator.Evaluate([new WatcherActivity("Plex", 1, Reachable: true)]);

        Assert.True(decision.Active);
        Assert.Contains("Plex", decision.Reason);
        Assert.Contains("1 stream", decision.Reason);
    }

    [Fact]
    public void Names_every_streaming_watcher_and_totals_the_sessions()
    {
        var decision = ActivityPauseEvaluator.Evaluate(
        [
            new WatcherActivity("Plex", 2, Reachable: true),
            new WatcherActivity("Jellyfin", 1, Reachable: true)
        ]);

        Assert.True(decision.Active);
        Assert.Contains("Plex", decision.Reason);
        Assert.Contains("Jellyfin", decision.Reason);
        Assert.Contains("3 streams", decision.Reason);
    }

    [Fact]
    public void An_unreachable_watcher_does_not_pause_the_queue()
    {
        var decision = ActivityPauseEvaluator.Evaluate([new WatcherActivity("Plex", 5, Reachable: false)]);

        Assert.False(decision.Active);
    }

    [Fact]
    public void An_idle_reachable_watcher_does_not_pause()
    {
        var decision = ActivityPauseEvaluator.Evaluate([new WatcherActivity("Emby", 0, Reachable: true)]);

        Assert.False(decision.Active);
    }
}
