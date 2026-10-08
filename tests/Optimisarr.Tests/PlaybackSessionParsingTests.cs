using Optimisarr.Core.Activity;

namespace Optimisarr.Tests;

public sealed class PlexPlaybackSessionTests
{
    // Shaped like a real /status/sessions response: attributes carry the item, children the viewer.
    private const string Sessions = """
        <MediaContainer size="3">
          <Video type="episode" title="Pilot" grandparentTitle="Example Show" parentIndex="2" index="5" year="2021">
            <User id="1" title="alex" />
            <Player title="Living Room TV" product="Plex for LG" state="playing" />
          </Video>
          <Video type="movie" title="Example Film" year="1999">
            <User id="2" title="sam" />
            <Player title="iPad" state="paused" />
          </Video>
          <Track type="track" title="Example Song" parentTitle="Example Album" grandparentTitle="Example Artist">
            <Player title="Kitchen" state="playing" />
          </Track>
        </MediaContainer>
        """;

    [Fact]
    public void Each_session_says_what_is_playing_who_is_watching_and_on_which_device()
    {
        var sessions = PlexSessionsParser.ParseSessions(Sessions);

        Assert.Equal(3, sessions.Count);
        Assert.Equal(new PlaybackSession(PlaybackKind.Episode, "Pilot", "Example Show", 2, 5, 2021, null, "alex", "Living Room TV", Paused: false), sessions[0]);
        Assert.Equal(new PlaybackSession(PlaybackKind.Movie, "Example Film", null, null, null, 1999, null, "sam", "iPad", Paused: true), sessions[1]);
        Assert.Equal(new PlaybackSession(PlaybackKind.Track, "Example Song", null, null, null, null, "Example Artist", null, "Kitchen", Paused: false), sessions[2]);
    }

    [Fact]
    public void A_compilation_track_names_its_own_artist_rather_than_the_album_artist()
    {
        const string xml = """
            <MediaContainer>
              <Track type="track" title="Example Song" grandparentTitle="Various Artists" originalTitle="Example Artist" />
            </MediaContainer>
            """;

        Assert.Equal("Example Artist", Assert.Single(PlexSessionsParser.ParseSessions(xml)).Artist);
    }

    [Fact]
    public void Missing_details_are_left_empty_rather_than_guessed()
    {
        var session = Assert.Single(PlexSessionsParser.ParseSessions("""<MediaContainer><Video type="clip" /></MediaContainer>"""));

        Assert.Equal(new PlaybackSession(PlaybackKind.Other, null, null, null, null, null, null, null, null, Paused: false), session);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml at all")]
    [InlineData("""<MediaContainer size="2" />""")]
    public void Unreadable_or_detail_free_responses_give_no_sessions(string xml) =>
        Assert.Empty(PlexSessionsParser.ParseSessions(xml));
}

public sealed class JellyfinPlaybackSessionTests
{
    private const string Sessions = """
        [
          { "UserName": "alex", "DeviceName": "Living Room TV", "Client": "Jellyfin Android TV",
            "PlayState": { "IsPaused": false },
            "NowPlayingItem": { "Type": "Episode", "Name": "Pilot", "SeriesName": "Example Show",
                                "ParentIndexNumber": 2, "IndexNumber": 5, "ProductionYear": 2021 } },
          { "UserName": "sam", "Client": "Jellyfin Web", "PlayState": { "IsPaused": true },
            "NowPlayingItem": { "Type": "Movie", "Name": "Example Film", "ProductionYear": 1999 } },
          { "UserName": "kim", "DeviceName": "Kitchen",
            "NowPlayingItem": { "Type": "Audio", "Name": "Example Song", "Artists": ["Example Artist"] } },
          { "UserName": "idle", "DeviceName": "Phone" }
        ]
        """;

    [Fact]
    public void Each_playing_session_says_what_who_and_where_and_idle_clients_are_skipped()
    {
        var sessions = JellyfinSessionsParser.ParseSessions(Sessions);

        Assert.Equal(3, sessions.Count);
        Assert.Equal(new PlaybackSession(PlaybackKind.Episode, "Pilot", "Example Show", 2, 5, 2021, null, "alex", "Living Room TV", Paused: false), sessions[0]);
        // No device name: the client stands in, which is what the server's own dashboard shows.
        Assert.Equal(new PlaybackSession(PlaybackKind.Movie, "Example Film", null, null, null, 1999, null, "sam", "Jellyfin Web", Paused: true), sessions[1]);
        Assert.Equal(new PlaybackSession(PlaybackKind.Track, "Example Song", null, null, null, null, "Example Artist", "kim", "Kitchen", Paused: false), sessions[2]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("{\"not\":\"an array\"}")]
    public void Unreadable_responses_give_no_sessions(string json) =>
        Assert.Empty(JellyfinSessionsParser.ParseSessions(json));
}

public sealed class PlaybackHoldTests
{
    private static readonly PlaybackSession Episode =
        new(PlaybackKind.Episode, "Pilot", "Example Show", 2, 5, null, null, "alex", "Living Room TV", Paused: false);

    [Fact]
    public void A_streaming_watcher_holds_the_queue_with_its_sessions_named()
    {
        var decision = ActivityPauseEvaluator.Evaluate([new WatcherActivity("Plex", 1, Reachable: true, [Episode])]);

        var hold = Assert.Single(decision.Holds);
        Assert.Equal(new PlaybackHold("Plex", Episode), hold);
    }

    [Fact]
    public void The_text_reason_stays_a_count_so_names_and_titles_never_reach_the_logs()
    {
        var decision = ActivityPauseEvaluator.Evaluate([new WatcherActivity("Plex", 1, Reachable: true, [Episode])]);

        Assert.Equal("Paused while Plex is active (1 stream).", decision.Reason);
        Assert.DoesNotContain("alex", decision.Reason);
        Assert.DoesNotContain("Pilot", decision.Reason);
    }

    [Fact]
    public void Idle_and_unreachable_watchers_contribute_no_holds()
    {
        var decision = ActivityPauseEvaluator.Evaluate(
        [
            new WatcherActivity("Plex", 0, Reachable: true, []),
            new WatcherActivity("Jellyfin", 1, Reachable: false, [Episode])
        ]);

        Assert.False(decision.Active);
        Assert.Empty(decision.Holds);
    }

    [Fact]
    public void Playback_is_named_only_while_it_is_what_holds_the_queue()
    {
        var streaming = ActivityPauseEvaluator.Evaluate([new WatcherActivity("Plex", 1, Reachable: true, [Episode])]);

        Assert.Single(ActivityPauseEvaluator.HoldsExplainingThePause(streaming, manuallyPaused: false));
        // The operator's pause outranks playback, so naming streams would point at the wrong cause.
        Assert.Empty(ActivityPauseEvaluator.HoldsExplainingThePause(streaming, manuallyPaused: true));
        Assert.Empty(ActivityPauseEvaluator.HoldsExplainingThePause(new ActivityDecision(false, null), manuallyPaused: false));
    }

    [Fact]
    public void Hiding_viewers_keeps_what_is_playing_and_drops_who_and_where()
    {
        Assert.Equal(Episode with { User = null, Device = null }, Episode.WithoutViewer());
    }
}
