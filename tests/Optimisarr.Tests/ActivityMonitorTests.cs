using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Optimisarr.Api.Queue;
using Optimisarr.Api.Replacement;
using Optimisarr.Core.Activity;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class ActivityMonitorTests : IDisposable
{
    private const string PlexSessions = """
        <MediaContainer size="1">
          <Video type="episode" title="Pilot" grandparentTitle="Example Show" parentIndex="1" index="1">
            <User title="alex" />
            <Player title="Living Room TV" state="playing" />
          </Video>
        </MediaContainer>
        """;

    private readonly SqliteConnection _connection = new("DataSource=:memory:;Foreign Keys=True");
    private readonly ServiceProvider _services;

    public ActivityMonitorTests()
    {
        _connection.Open();
        _services = new ServiceCollection()
            .AddDbContext<OptimisarrDbContext>(options => options.UseSqlite(_connection))
            .BuildServiceProvider();
        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().Database.Migrate();
    }

    [Fact]
    public async Task A_streaming_server_holds_the_queue_with_what_is_playing_and_who_is_watching()
    {
        await SeedAsync("Plex", showViewerNames: true);

        var decision = await MonitorAsync();

        Assert.True(decision.Active);
        var hold = Assert.Single(decision.Holds);
        Assert.Equal("Plex", hold.Watcher);
        Assert.Equal("Example Show", hold.Session.Series);
        Assert.Equal("alex", hold.Session.User);
        Assert.Equal("Living Room TV", hold.Session.Device);
        Assert.DoesNotContain("alex", decision.Reason);
    }

    [Fact]
    public async Task A_watcher_that_hides_viewers_names_the_title_but_not_who_or_where()
    {
        await SeedAsync("Plex", showViewerNames: false);

        var hold = Assert.Single((await MonitorAsync()).Holds);

        Assert.Equal("Pilot", hold.Session.Title);
        Assert.Null(hold.Session.User);
        Assert.Null(hold.Session.Device);
    }

    [Fact]
    public async Task Hiding_viewers_takes_effect_at_once_rather_than_after_the_cached_poll_expires()
    {
        await SeedAsync("Plex", showViewerNames: true);
        var monitor = Monitor(new PlexHandler());
        Assert.Equal("alex", Assert.Single((await monitor.GetActivityAsync(CancellationToken.None)).Holds).Session.User);

        await SetShowViewerNamesAsync(false);
        monitor.Invalidate();

        Assert.Null(Assert.Single((await monitor.GetActivityAsync(CancellationToken.None)).Holds).Session.User);
    }

    [Fact]
    public async Task A_poll_already_in_flight_cannot_restore_names_hidden_while_it_ran()
    {
        await SeedAsync("Plex", showViewerNames: true);
        var gate = new GatedPlexHandler();
        var monitor = Monitor(gate);

        var inFlight = monitor.GetActivityAsync(CancellationToken.None);
        await gate.FirstRequestStarted.Task;
        await SetShowViewerNamesAsync(false);
        monitor.Invalidate();
        gate.Release.SetResult();

        Assert.Null(Assert.Single((await inFlight).Holds).Session.User);
        Assert.Null(Assert.Single((await monitor.GetActivityAsync(CancellationToken.None)).Holds).Session.User);
    }

    [Fact]
    public async Task Hiding_viewers_during_cache_publication_cannot_restore_the_old_poll()
    {
        await SeedAsync("Plex", showViewerNames: true);
        using var clock = new PublicationClock();
        var monitor = new ActivityMonitor(_services.GetRequiredService<IServiceScopeFactory>(),
            new StubHttpClientFactory(new PlexHandler()), clock, NullLogger<ActivityMonitor>.Instance);
        var inFlight = Task.Run(() => monitor.GetActivityAsync(CancellationToken.None));
        try
        {
            await clock.Publishing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await SetShowViewerNamesAsync(false);
            monitor.Invalidate();
        }
        finally
        {
            clock.Release.Set();
        }
        Assert.Null(Assert.Single((await inFlight).Holds).Session.User);
        Assert.Null(Assert.Single((await monitor.GetActivityAsync(CancellationToken.None)).Holds).Session.User);
    }

    [Theory]
    [InlineData(ActivityWatcherType.Plex, "X-Plex-Token")]
    [InlineData(ActivityWatcherType.Jellyfin, "X-Emby-Token")]
    [InlineData(ActivityWatcherType.Emby, "X-Emby-Token")]
    public async Task Playback_art_is_proxied_from_the_exact_watcher_with_header_only_credentials(
        ActivityWatcherType type, string header)
    {
        await SeedAsync("Media", showViewerNames: false);
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            (await db.ActivityWatchers.SingleAsync()).Type = type;
            await db.SaveChangesAsync();
        }
        var handler = new PlaybackImageHandler(type);
        var factory = new StubHttpClientFactory(handler);
        var monitor = Monitor(handler);
        var service = new ArtworkService(_services.GetRequiredService<IServiceScopeFactory>(), factory, new TranscodeOptions("unused"));
        var hold = Assert.Single((await monitor.GetActivityAsync(CancellationToken.None)).Holds);
        Assert.Null(hold.Session.User);
        var key = hold.Session.Artwork!.ProxyKey(hold.WatcherId!.Value);
        Assert.NotNull(await service.GetPlaybackAsync(key, monitor, CancellationToken.None));
        Assert.Equal("tok", handler.ImageRequest!.Headers.GetValues(header).Single());
        Assert.DoesNotContain("tok", handler.ImageRequest.RequestUri!.ToString());
        Assert.Equal(1, handler.ImageRequests);
        Assert.Null(await service.GetPlaybackAsync(new string('A', 64), monitor, CancellationToken.None));
        Assert.Equal(1, handler.ImageRequests);

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            (await db.ActivityWatchers.SingleAsync()).Enabled = false;
            await db.SaveChangesAsync();
        }
        // Even a cached poll cannot keep a disabled watcher available to the image proxy.
        Assert.Null(await service.GetPlaybackAsync(key, monitor, CancellationToken.None));
        Assert.Equal(1, handler.ImageRequests);
    }

    [Theory]
    [InlineData("text/html", HttpStatusCode.OK, false)]
    [InlineData("image/jpeg", HttpStatusCode.Redirect, false)]
    [InlineData("image/jpeg", HttpStatusCode.OK, true)]
    public async Task Failed_non_image_and_oversized_art_leave_the_playback_hold_intact(
        string contentType, HttpStatusCode status, bool oversized)
    {
        await SeedAsync("Plex", showViewerNames: true);
        var handler = new PlaybackImageHandler(ActivityWatcherType.Plex, contentType, status, oversized);
        var monitor = Monitor(handler);
        var service = new ArtworkService(_services.GetRequiredService<IServiceScopeFactory>(),
            new StubHttpClientFactory(handler), new TranscodeOptions("unused"));
        var decision = await monitor.GetActivityAsync(CancellationToken.None);
        var hold = Assert.Single(decision.Holds);
        Assert.Null(await service.GetPlaybackAsync(hold.Session.Artwork!.ProxyKey(hold.WatcherId!.Value), monitor, CancellationToken.None));
        Assert.True((await monitor.GetActivityAsync(CancellationToken.None)).Active);
        Assert.Equal(decision.Reason, (await monitor.GetActivityAsync(CancellationToken.None)).Reason);
    }

    private sealed class PlaybackImageHandler(ActivityWatcherType type, string contentType = "image/jpeg",
        HttpStatusCode status = HttpStatusCode.OK, bool oversized = false) : HttpMessageHandler
    {
        public HttpRequestMessage? ImageRequest { get; private set; }
        public int ImageRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath is "/status/sessions" or "/Sessions")
            {
                var body = type == ActivityWatcherType.Plex
                    ? PlexSessions.Replace("title=\"Pilot\"", "title=\"Pilot\" grandparentThumb=\"/library/metadata/1/thumb/2\"")
                    : """[{"UserName":"alex","NowPlayingItem":{"Type":"Movie","Id":"11111111222233334444555555555555","ImageTags":{"Primary":"abc"}}}]""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            }
            ImageRequests++;
            ImageRequest = request;
            var content = new ByteArrayContent([0xff, 0xd8, 0xff, 0xd9]);
            content.Headers.ContentType = new(contentType);
            if (oversized) content.Headers.ContentLength = 11 * 1024 * 1024;
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }

    private sealed class PublicationClock : TimeProvider, IDisposable
    {
        private int _calls;
        public TaskCompletionSource Publishing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Increment(ref _calls) == 3)
            {
                Publishing.SetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Cache publication was not released");
            }
            return DateTimeOffset.UtcNow;
        }
        public void Dispose() => Release.Dispose();
    }

    private ActivityMonitor Monitor(HttpMessageHandler handler) =>
        new(_services.GetRequiredService<IServiceScopeFactory>(), new StubHttpClientFactory(handler),
            TimeProvider.System, NullLogger<ActivityMonitor>.Instance);

    private async Task<ActivityDecision> MonitorAsync() =>
        await Monitor(new PlexHandler()).GetActivityAsync(CancellationToken.None);

    private async Task SetShowViewerNamesAsync(bool show)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        (await db.ActivityWatchers.SingleAsync()).ShowViewerNames = show;
        await db.SaveChangesAsync();
    }

    private async Task SeedAsync(string name, bool showViewerNames)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        db.ActivityWatchers.Add(new ActivityWatcher
        {
            Name = name, Type = ActivityWatcherType.Plex, BaseUrl = "http://plex:32400", ApiToken = "tok",
            ShowViewerNames = showViewerNames
        });
        await db.SaveChangesAsync();
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    // Holds the first response until released, so a watcher can change while that poll runs.
    private sealed class GatedPlexHandler : HttpMessageHandler
    {
        private int _requests;
        public TaskCompletionSource FirstRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requests) == 1)
            {
                FirstRequestStarted.SetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(PlexSessions) };
        }
    }

    private sealed class PlexHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(PlexSessions) });
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }
}
