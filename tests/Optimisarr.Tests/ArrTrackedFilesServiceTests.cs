using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Optimisarr.Api.Library;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class ArrTrackedFilesServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;

    public ArrTrackedFilesServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.Migrate();
    }

    public void Dispose() => _connection.Dispose();

    private async Task<(int Film, int Episode, int Spare)> SeedAsync(params ArrConnection[] connections)
    {
        await using var db = new OptimisarrDbContext(_options);
        var library = new Library { Name = "All", Path = "/data" };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        MediaFile File(string path) => new() { LibraryId = library.Id, Path = path, RelativePath = path[6..], SizeBytes = 1, ModifiedAt = DateTimeOffset.UtcNow };
        var film = File("/data/Film/A (2020)/A (2020).mkv");
        var episode = File("/data/TV/S/Season 1/S E1.mkv");
        var spare = File("/data/Spare/B.mkv");
        db.MediaFiles.AddRange(film, episode, spare);
        db.ArrConnections.AddRange(connections);
        await db.SaveChangesAsync();
        return (film.Id, episode.Id, spare.Id);
    }

    private static ArrConnection Radarr() => new() { Name = "Radarr", Type = ArrConnectionType.Radarr, BaseUrl = "http://radarr:7878/", ApiKey = "secret" };
    private static ArrConnection Sonarr() => new() { Name = "Sonarr", Type = ArrConnectionType.Sonarr, BaseUrl = "http://sonarr:8989", ApiKey = "secret" };

    private ArrTrackedFilesService Service(Handler handler) =>
        new(new OptimisarrDbContext(_options), new Factory(handler), NullLogger<ArrTrackedFilesService>.Instance);

    [Fact]
    public async Task Both_managers_are_asked_and_each_file_is_attributed()
    {
        var (film, episode, spare) = await SeedAsync(Radarr(), Sonarr());
        var handler = new Handler(new()
        {
            ["http://radarr:7878/api/v3/movie"] = """[{ "movieFile": { "path": "/movies/A (2020)/A (2020).mkv" } }]""",
            ["http://sonarr:8989/api/v3/series"] = """[{ "id": 4 }]""",
            ["http://sonarr:8989/api/v3/episodefile?seriesId=4"] = """[{ "path": "/tv/S/Season 1/S E1.mkv" }]""",
        });

        var tracking = await Service(handler).GetAsync(CancellationToken.None);

        Assert.True(tracking.Complete);
        Assert.Equal(["Radarr"], tracking.TrackedBy[film]);
        Assert.Equal(["Sonarr"], tracking.TrackedBy[episode]);
        Assert.False(tracking.TrackedBy.ContainsKey(spare));
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.All(handler.Requests, request => Assert.Equal("secret", request.Headers.GetValues("X-Api-Key").Single()));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "[]")]
    [InlineData(HttpStatusCode.OK, "<html>a proxy login page</html>")]
    [InlineData(HttpStatusCode.OK, "{ \"message\": \"nope\" }")]
    public async Task A_manager_that_cannot_answer_leaves_the_result_incomplete(HttpStatusCode status, string body)
    {
        await SeedAsync(Radarr());

        var tracking = await Service(new Handler(new() { ["http://radarr:7878/api/v3/movie"] = body }, status)).GetAsync(CancellationToken.None);

        Assert.False(tracking.Complete);
        Assert.False(Assert.Single(tracking.Connections).Reachable);
    }

    [Fact]
    public async Task One_series_that_fails_leaves_the_result_incomplete_rather_than_partial()
    {
        await SeedAsync(Sonarr());
        var handler = new Handler(new()
        {
            ["http://sonarr:8989/api/v3/series"] = """[{ "id": 4 }, { "id": 5 }]""",
            ["http://sonarr:8989/api/v3/episodefile?seriesId=4"] = """[{ "path": "/tv/S/Season 1/S E1.mkv" }]""",
        });

        Assert.False((await Service(handler).GetAsync(CancellationToken.None)).Complete);
    }

    [Fact]
    public async Task A_disabled_manager_is_not_asked()
    {
        var disabled = Radarr();
        disabled.Enabled = false;
        await SeedAsync(disabled);
        var handler = new Handler([]);

        var tracking = await Service(handler).GetAsync(CancellationToken.None);

        Assert.Empty(handler.Requests);
        Assert.True(tracking.Complete);
    }

    private sealed class Handler(Dictionary<string, string> answers, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(answers.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
