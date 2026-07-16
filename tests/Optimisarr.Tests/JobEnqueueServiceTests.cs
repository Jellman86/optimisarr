using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Optimisarr.Api.Library;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class JobEnqueueServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;

    public JobEnqueueServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Enqueues_eligible_files_and_skips_ineligible()
    {
        var libraryId = await SeedLibraryWithFilesAsync();

        var result = await EnqueueAsync(libraryId);

        Assert.Equal(1, result.Enqueued);    // only the h264 file
        Assert.Equal(1, result.Ineligible);  // the already-hevc file

        await using var db = new OptimisarrDbContext(_options);
        var job = Assert.Single(db.Jobs);
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(2, job.Priority);       // snapshotted from the library
    }

    [Fact]
    public async Task Is_idempotent_for_files_with_an_active_job()
    {
        var libraryId = await SeedLibraryWithFilesAsync();

        var first = await EnqueueAsync(libraryId);
        var second = await EnqueueAsync(libraryId);

        Assert.Equal(1, first.Enqueued);
        Assert.Equal(0, second.Enqueued);
        Assert.Equal(1, second.AlreadyQueued);

        await using var db = new OptimisarrDbContext(_options);
        Assert.Single(db.Jobs);
    }

    [Fact]
    public async Task A_disposable_calibration_job_does_not_block_normal_enqueue()
    {
        var libraryId = await SeedLibraryWithFilesAsync();
        await using (var db = new OptimisarrDbContext(_options))
        {
            var fileId = await db.MediaFiles
                .Where(file => file.LibraryId == libraryId && file.VideoCodec == "h264")
                .Select(file => file.Id)
                .SingleAsync();
            db.Jobs.Add(new Job
            {
                MediaFileId = fileId,
                Type = JobType.Calibration,
                Status = JobStatus.Queued
            });
            await db.SaveChangesAsync();
        }

        var result = await EnqueueAsync(libraryId);

        Assert.Equal(1, result.Enqueued);
        await using var readDb = new OptimisarrDbContext(_options);
        Assert.Contains(readDb.Jobs, job => job.Type == JobType.Normal);
        Assert.Contains(readDb.Jobs, job => job.Type == JobType.Calibration);
    }

    [Fact]
    public async Task Holds_back_a_file_a_connected_arr_is_importing_into()
    {
        var libraryId = await SeedLibraryWithFilesAsync();
        await using (var db = new OptimisarrDbContext(_options))
        {
            db.ArrConnections.Add(new ArrConnection
            {
                Name = "Radarr", Type = ArrConnectionType.Radarr, BaseUrl = "http://radarr:7878", ApiKey = "k"
            });
            await db.SaveChangesAsync();
        }

        // Radarr reports it is importing into the folder holding the eligible file.
        var queueJson = """
        { "records": [ { "movie": { "path": "/data/films" } } ] }
        """;
        var result = await EnqueueAsync(libraryId, new StubHandler(queueJson));

        Assert.Equal(0, result.Enqueued);
        Assert.Equal(1, result.Importing);

        await using var readDb = new OptimisarrDbContext(_options);
        Assert.Empty(readDb.Jobs);
    }

    private async Task<int> SeedLibraryWithFilesAsync()
    {
        await using var db = new OptimisarrDbContext(_options);
        var library = new Library
        {
            Name = "Films",
            Path = "/data/films",
            RuleProfile = RuleProfile.ConservativeHevc,
            Priority = 2
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();

        db.MediaFiles.Add(Probed(library.Id, "a.mkv", "h264"));   // eligible -> hevc
        db.MediaFiles.Add(Probed(library.Id, "b.mkv", "hevc"));   // already hevc -> skipped
        await db.SaveChangesAsync();
        return library.Id;
    }

    private async Task<EnqueueResult> EnqueueAsync(int libraryId, HttpMessageHandler? handler = null)
    {
        await using var db = new OptimisarrDbContext(_options);
        var library = await db.Libraries.SingleAsync(l => l.Id == libraryId);
        var arr = new ArrActivityService(
            db,
            new StubHttpClientFactory(handler ?? new StubHandler("""{ "records": [] }""")),
            NullLogger<ArrActivityService>.Instance);
        var service = new JobEnqueueService(db, new CandidateService(db), arr);
        return await service.EnqueueEligibleAsync(library, CancellationToken.None);
    }

    private static MediaFile Probed(int libraryId, string relativePath, string videoCodec) => new()
    {
        LibraryId = libraryId,
        Path = $"/data/films/{relativePath}",
        RelativePath = relativePath,
        SizeBytes = 5_000_000_000,
        Status = MediaFileStatus.Probed,
        Container = "matroska,webm",
        VideoCodec = videoCodec,
        Width = 1920,
        Height = 1080,
        ProbedAt = DateTimeOffset.UtcNow
    };

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    public void Dispose() => _connection.Dispose();
}
