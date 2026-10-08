using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Optimisarr.Api.Queue;
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
        scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().Database.EnsureCreated();
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

    private async Task<ActivityDecision> MonitorAsync()
    {
        var monitor = new ActivityMonitor(_services.GetRequiredService<IServiceScopeFactory>(),
            new StubHttpClientFactory(new PlexHandler()), TimeProvider.System, NullLogger<ActivityMonitor>.Instance);
        return await monitor.GetActivityAsync(CancellationToken.None);
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
