using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Optimisarr.Api.Replacement;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class LibraryRefreshServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;

    public LibraryRefreshServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Notifies_an_enabled_refresh_watcher_with_the_right_request()
    {
        await SeedWatcherAsync(ActivityWatcherType.Plex, "http://plex:32400", "tok", enabled: true, refresh: true);
        var handler = new RecordingHandler();

        await RefreshAsync(handler, "/data/Movies/Heat/Heat.mkv");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("http://plex:32400/library/sections/all/refresh", request.Uri);
        Assert.Equal("tok", request.PlexToken);
    }

    [Fact]
    public async Task Skips_disabled_and_non_refresh_watchers()
    {
        await SeedWatcherAsync(ActivityWatcherType.Plex, "http://a:32400", "t", enabled: false, refresh: true);
        await SeedWatcherAsync(ActivityWatcherType.Jellyfin, "http://b:8096", "t", enabled: true, refresh: false);
        var handler = new RecordingHandler();

        await RefreshAsync(handler, "/data/x.mkv");

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_failing_server_does_not_throw()
    {
        await SeedWatcherAsync(ActivityWatcherType.Emby, "http://emby:8096", "t", enabled: true, refresh: true);
        var handler = new ThrowingHandler();

        // Best effort: must complete without surfacing the transport error.
        await RefreshAsync(handler, "/data/x.mkv");
    }

    private async Task RefreshAsync(HttpMessageHandler handler, string path)
    {
        await using var db = new OptimisarrDbContext(_options);
        var service = new LibraryRefreshService(
            db, new StubHttpClientFactory(handler), NullLogger<LibraryRefreshService>.Instance);
        await service.RefreshForPathAsync(path, CancellationToken.None);
    }

    private async Task SeedWatcherAsync(
        ActivityWatcherType type, string baseUrl, string token, bool enabled, bool refresh)
    {
        await using var db = new OptimisarrDbContext(_options);
        db.ActivityWatchers.Add(new ActivityWatcher
        {
            Name = $"{type}",
            Type = type,
            BaseUrl = baseUrl,
            ApiToken = token,
            Enabled = enabled,
            RefreshOnReplace = refresh
        });
        await db.SaveChangesAsync();
    }

    private sealed record CapturedRequest(HttpMethod Method, string Uri, string? PlexToken);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = request.Headers.TryGetValues("X-Plex-Token", out var values) ? values.FirstOrDefault() : null;
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!.ToString(), token));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    public void Dispose() => _connection.Dispose();
}
