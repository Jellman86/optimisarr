using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Optimisarr.Api.Replacement;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class ArrRescanServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;

    public ArrRescanServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Asks_radarr_to_rescan_the_movie_whose_folder_holds_the_file()
    {
        await SeedAsync(ArrConnectionType.Radarr, "http://radarr:7878/");
        var handler = new ManagerHandler("""[{"id":4,"path":"/movies/Ronin (1998)"},{"id":9,"path":"/movies/Heat (1995)"}]""");

        await RescanAsync(handler, "/media/films/Heat (1995)/Heat (1995).mp4");

        Assert.Equal(["GET http://radarr:7878/api/v3/movie", "POST http://radarr:7878/api/v3/command"], handler.Requests);
        Assert.Equal("""{"name":"RescanMovie","movieId":9}""", handler.CommandBody);
        Assert.All(handler.ApiKeys, key => Assert.Equal("key", key));
    }

    [Fact]
    public async Task Asks_sonarr_to_rescan_the_series_holding_the_episode()
    {
        await SeedAsync(ArrConnectionType.Sonarr, "http://sonarr:8989");
        var handler = new ManagerHandler("""[{"id":2,"path":"/tv/The Wire"}]""");

        await RescanAsync(handler, "/media/tv/The Wire/Season 01/The Wire - S01E01.mkv");

        Assert.Equal(["GET http://sonarr:8989/api/v3/series", "POST http://sonarr:8989/api/v3/command"], handler.Requests);
        Assert.Equal("""{"name":"RescanSeries","seriesId":2}""", handler.CommandBody);
    }

    [Fact]
    public async Task Sends_nothing_when_no_single_title_holds_the_file()
    {
        await SeedAsync(ArrConnectionType.Radarr, "http://radarr:7878");
        var handler = new ManagerHandler("""[{"id":1,"path":"/a/Heat (1995)"},{"id":2,"path":"/b/Heat (1995)"}]""");

        await RescanAsync(handler, "/media/c/Heat (1995)/Heat.mkv");

        Assert.Equal(["GET http://radarr:7878/api/v3/movie"], handler.Requests);
    }

    [Fact]
    public async Task Skips_connections_that_are_disabled_or_not_asked_to_rescan()
    {
        await SeedAsync(ArrConnectionType.Radarr, "http://a:7878", enabled: false);
        await SeedAsync(ArrConnectionType.Radarr, "http://b:7878", rescan: false);
        var handler = new ManagerHandler("[]");

        await RescanAsync(handler, "/media/Heat (1995)/Heat.mkv");

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_manager_that_cannot_be_reached_does_not_throw()
    {
        await SeedAsync(ArrConnectionType.Radarr, "http://radarr:7878");

        await RescanAsync(new ThrowingHandler(), "/media/Heat (1995)/Heat.mkv");
    }

    [Fact]
    public async Task Media_server_refresh_also_asks_the_managers()
    {
        await SeedAsync(ArrConnectionType.Radarr, "http://radarr:7878");
        var handler = new ManagerHandler("""[{"id":9,"path":"/movies/Heat (1995)"}]""");
        await using var db = new OptimisarrDbContext(_options);
        var factory = new StubHttpClientFactory(handler);
        var refresh = new LibraryRefreshService(db, factory, NullLogger<LibraryRefreshService>.Instance,
            new ArrRescanService(db, factory, NullLogger<ArrRescanService>.Instance));

        await refresh.RefreshForPathAsync("/media/Heat (1995)/Heat.mkv", CancellationToken.None);

        Assert.Contains("POST http://radarr:7878/api/v3/command", handler.Requests);
    }

    private async Task RescanAsync(HttpMessageHandler handler, string path)
    {
        await using var db = new OptimisarrDbContext(_options);
        var service = new ArrRescanService(db, new StubHttpClientFactory(handler), NullLogger<ArrRescanService>.Instance);
        await service.RescanForPathAsync(path, CancellationToken.None);
    }

    private async Task SeedAsync(ArrConnectionType type, string baseUrl, bool enabled = true, bool rescan = true)
    {
        await using var db = new OptimisarrDbContext(_options);
        db.ArrConnections.Add(new ArrConnection
        {
            Name = baseUrl,
            Type = type,
            BaseUrl = baseUrl,
            ApiKey = "key",
            Enabled = enabled,
            RescanAfterChanges = rescan
        });
        await db.SaveChangesAsync();
    }

    private sealed class ManagerHandler(string titles) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<string?> ApiKeys { get; } = [];
        public string? CommandBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri}");
            ApiKeys.Add(request.Headers.TryGetValues("X-Api-Key", out var keys) ? keys.Single() : null);
            if (request.Method == HttpMethod.Post)
            {
                CommandBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(titles, Encoding.UTF8, "application/json") };
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
