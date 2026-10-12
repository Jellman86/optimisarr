using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Optimisarr.Api.Deduplication;
using Optimisarr.Api.Library;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Deduplication;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Data;

namespace Optimisarr.Tests;

[Collection(TokenedApiCollection.Name)]
public sealed class DedupeDisposableCleanupTests(AdminTokenAuthEndpointTests.TokenedApi api)
{
    [Theory]
    [InlineData("media", JobStatus.Completed)]
    [InlineData("descendant", JobStatus.Completed)]
    [InlineData("identity", JobStatus.Completed)]
    [InlineData("missing", JobStatus.Completed)]
    [InlineData("unrelated", JobStatus.Completed)]
    [InlineData("descendant", JobStatus.Failed)]
    [InlineData("unknown", JobStatus.Failed)]
    public async Task Preview_cleanup_preserves_files_and_diagnostics_until_every_reservation_is_resolved(string keyKind, JobStatus status)
    {
        await using var fixture = await Fixture.CreateAsync(api);
        var jobId = await fixture.AddPreviewAsync(status);
        var root = WorkOutputRoot.ForPreview(fixture.WorkRoot, jobId);
        var output = Path.Combine(root, "nested", "output.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, "preview evidence");
        await fixture.SetOutputAsync(jobId, output);
        await fixture.ReserveAsync(keyKind switch
        {
            "media" => FileClaimKeys.Media(fixture.MediaId),
            "identity" => FileOperationClaims.Keys(fixture.MediaId, output).Single(k => k.StartsWith("identity:", StringComparison.Ordinal)),
            "missing" => FileClaimKeys.Path(Path.Combine(root, "absent", "future.mkv"), OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()),
            "unrelated" => FileClaimKeys.Media(int.MaxValue),
            _ => FileClaimKeys.Path(output, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        }, unknown: keyKind == "unknown");
        using var response = await fixture.Client.DeleteAsync($"/api/preview/{jobId}");
        await AssertDeferredAsync(response);
        Assert.Equal("preview evidence", await File.ReadAllTextAsync(output));
        await fixture.AssertJobAsync(jobId, status, output);
        await fixture.ReleaseAsync();
        using var retry = await fixture.Client.DeleteAsync($"/api/preview/{jobId}");
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        Assert.False(Directory.Exists(root));
        using var scope = api.Services.CreateScope();
        var job = await scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId);
        if (status == JobStatus.Failed)
        {
            Assert.NotNull(job);
            Assert.Null(job.WorkOutputPath);
            Assert.Equal("diagnostic evidence", job.ErrorMessage);
        }
        else Assert.Null(job);
    }

    [Fact]
    public async Task Busy_file_operations_defer_preview_cleanup_and_recreation_without_removing_the_previous_preview()
    {
        await using var fixture = await Fixture.CreateAsync(api);
        var jobId = await fixture.AddPreviewAsync(JobStatus.Completed);
        var claims = api.Services.GetRequiredService<FileOperationClaims>();
        using (var held = claims.TryAcquire(["unrelated operation"]))
        {
            Assert.NotNull(held);
            using var deletion = await fixture.Client.DeleteAsync($"/api/preview/{jobId}");
            await AssertDeferredAsync(deletion);
            using var creation = await fixture.Client.PostAsync($"/api/media/{fixture.MediaId}/preview", null);
            await AssertDeferredAsync(creation);
            await fixture.AssertJobAsync(jobId, JobStatus.Completed, null);
        }
        using var retry = await fixture.Client.PostAsync($"/api/media/{fixture.MediaId}/preview", null);
        retry.EnsureSuccessStatusCode();
        var body = await retry.Content.ReadFromJsonAsync<JsonElement>();
        using var scope = api.Services.CreateScope();
        var jobs = await scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().Jobs.AsNoTracking().Where(j => j.LibraryId == fixture.LibraryId).ToListAsync();
        Assert.Single(jobs);
        Assert.Equal(body.GetProperty("jobId").GetInt32(), jobs[0].Id);
        Assert.Equal(JobStatus.Queued, jobs[0].Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deferred_calibration_cleanup_keeps_the_session_and_failed_job_evidence_available_for_retry(bool busy)
    {
        await using var fixture = await Fixture.CreateAsync(api);
        using var created = await fixture.Client.PostAsJsonAsync($"/api/libraries/{fixture.LibraryId}/calibration", new { mediaFileId = fixture.MediaId });
        created.EnsureSuccessStatusCode();
        var session = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = session.GetProperty("id").GetGuid();
        var root = Path.Combine(fixture.WorkRoot, "calibration", id.ToString("N"));
        var output = Path.Combine(root, "nested", ".optimisarr-comparison-reference.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, "calibration evidence");
        var jobId = await fixture.FailCalibrationAsync(id, output);
        using var held = busy ? api.Services.GetRequiredService<FileOperationClaims>().TryAcquire(["in-flight preparation"]) : null;
        if (!busy) await fixture.ReserveAsync(FileClaimKeys.Path(output, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()));
        using var response = await fixture.Client.DeleteAsync($"/api/calibration/{id}");
        await AssertDeferredAsync(response);
        using var get = await fixture.Client.GetAsync($"/api/calibration/{id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var sessions = await fixture.Client.GetFromJsonAsync<JsonElement>("/api/calibration");
        Assert.Contains(sessions.EnumerateArray(), s => s.GetProperty("id").GetGuid() == id);
        Assert.Equal("calibration evidence", await File.ReadAllTextAsync(output));
        await fixture.AssertJobAsync(jobId, JobStatus.Failed, output, id);
        held?.Dispose();
        await fixture.ReleaseAsync();
        using var retry = await fixture.Client.DeleteAsync($"/api/calibration/{id}");
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        Assert.False(Directory.Exists(root));
        await fixture.AssertJobAsync(jobId, JobStatus.Failed, null);
        using var gone = await fixture.Client.GetAsync($"/api/calibration/{id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Expiration_defers_a_reserved_calibration_and_retries_without_losing_the_session_or_diagnostics()
    {
        await using var fixture = await Fixture.CreateAsync(api);
        var clock = new ManualClock();
        var services = api.Services;
        var environment = new WorkEnvironment(fixture.Root);
        using var service = new BlindCalibrationService(services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<QueueDispatcher>(), environment, clock,
            services.GetRequiredService<ICalibrationRandomizer>(), services.GetRequiredService<IMediaProbeService>(),
            services.GetRequiredService<LoudnessService>(), services.GetRequiredService<FileOperationClaims>());
        var session = await service.CreateAsync(fixture.LibraryId, fixture.MediaId, false, false, false, default);
        var root = Path.Combine(WorkPaths.Resolve(environment), "calibration", session.Id.ToString("N"));
        Directory.CreateDirectory(root);
        var output = Path.Combine(root, "failed.mkv");
        await File.WriteAllTextAsync(output, "expired evidence");
        var jobId = await fixture.FailCalibrationAsync(session.Id, output);
        await fixture.ReserveAsync(FileClaimKeys.Media(int.MaxValue));
        clock.Now = clock.Now.AddHours(3);
        Assert.Single(await service.ListAsync(default));
        Assert.Equal("expired evidence", await File.ReadAllTextAsync(output));
        await fixture.AssertJobAsync(jobId, JobStatus.Failed, output, session.Id);
        await fixture.ReleaseAsync();
        Assert.Empty(await service.ListAsync(default));
        Assert.False(Directory.Exists(root));
        await fixture.AssertJobAsync(jobId, JobStatus.Failed, null);
    }

    [Fact]
    public async Task Missing_preview_and_session_remain_not_found_even_when_cleanup_is_blocked()
    {
        await using var fixture = await Fixture.CreateAsync(api);
        await fixture.ReserveAsync(FileClaimKeys.Media(int.MaxValue));
        using var preview = await fixture.Client.DeleteAsync("/api/preview/2147483647");
        using var calibration = await fixture.Client.DeleteAsync($"/api/calibration/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, preview.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, calibration.StatusCode);
    }

    private static async Task AssertDeferredAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("work.cleanupDeferred", error.GetProperty("code").GetString());
        Assert.Contains("retry", error.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class WorkEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Optimisarr";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class Fixture(AdminTokenAuthEndpointTests.TokenedApi api, string root, int libraryId, int mediaId) : IAsyncDisposable
    {
        public string Root => root;
        public int LibraryId => libraryId;
        public int MediaId => mediaId;
        public string WorkRoot => WorkPaths.Resolve(api.Services.GetRequiredService<IHostEnvironment>());
        public HttpClient Client { get; } = CreateClient(api);
        private Guid? _actionId;
        private readonly List<string> _scratch = [];
        private static HttpClient CreateClient(AdminTokenAuthEndpointTests.TokenedApi api)
        {
            var client = api.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
            return client;
        }
        public static async Task<Fixture> CreateAsync(AdminTokenAuthEndpointTests.TokenedApi api)
        {
            var root = Path.Combine(api.LibraryDirectory, "disposable-" + Guid.NewGuid());
            Directory.CreateDirectory(root);
            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var library = new Library { Name = "Disposable fixtures", Path = root };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            var media = new MediaFile { LibraryId = library.Id, Path = Path.Combine(root, "source.mkv"), RelativePath = "source.mkv",
                Status = MediaFileStatus.Probed, MediaKind = MediaKind.Video, SizeBytes = 42, DurationSeconds = 300, VideoCodec = "h264" };
            db.MediaFiles.Add(media);
            await db.SaveChangesAsync();
            await File.WriteAllTextAsync(media.Path, "unchanged original");
            return new(api, root, library.Id, media.Id);
        }
        public async Task<int> AddPreviewAsync(JobStatus status)
        {
            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var job = new Job { MediaFileId = MediaId, LibraryId = LibraryId, Type = JobType.Preview, Status = status, ErrorMessage = "diagnostic evidence" };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            _scratch.Add(WorkOutputRoot.ForPreview(WorkRoot, job.Id));
            return job.Id;
        }
        public async Task SetOutputAsync(int jobId, string output)
        {
            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            (await db.Jobs.SingleAsync(j => j.Id == jobId)).WorkOutputPath = output;
            await db.SaveChangesAsync();
        }
        public async Task<int> FailCalibrationAsync(Guid id, string output)
        {
            _scratch.Add(Path.Combine(WorkRoot, "calibration", id.ToString("N")));
            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var jobs = await db.Jobs.Where(j => j.CalibrationSessionId == id).ToListAsync();
            Assert.NotEmpty(jobs);
            foreach (var job in jobs) { job.Status = JobStatus.Failed; job.ErrorMessage = "diagnostic evidence"; }
            jobs[0].WorkOutputPath = output;
            await db.SaveChangesAsync();
            return jobs[0].Id;
        }
        public async Task ReserveAsync(string key, bool unknown = false)
        {
            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            _actionId = Guid.NewGuid();
            db.DedupeActions.Add(new() { Id = _actionId.Value, State = unknown ? (DedupeActionState)999 : DedupeActionState.Prepared,
                ReviewKey = new('a', 64), ScopeKey = new('b', 64), PlanHash = new('c', 64) });
            db.DedupeFileClaims.Add(new() { ActionId = _actionId.Value, Key = key });
            await db.SaveChangesAsync();
        }
        public async Task ReleaseAsync()
        {
            if (_actionId is not { } id) return;
            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            db.DedupeFileClaims.RemoveRange(await db.DedupeFileClaims.Where(c => c.ActionId == id).ToListAsync());
            await db.SaveChangesAsync();
            db.DedupeActions.Remove(await db.DedupeActions.SingleAsync(a => a.Id == id));
            await db.SaveChangesAsync();
            _actionId = null;
        }
        public async Task AssertJobAsync(int jobId, JobStatus status, string? output, Guid? sessionId = null)
        {
            using var scope = api.Services.CreateScope();
            var job = await scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
            Assert.Equal(status, job.Status);
            Assert.Equal(output, job.WorkOutputPath);
            Assert.Equal(sessionId, job.CalibrationSessionId);
            Assert.Equal("diagnostic evidence", job.ErrorMessage);
            Assert.Equal("unchanged original", await File.ReadAllTextAsync(Path.Combine(Root, "source.mkv")));
        }
        public async ValueTask DisposeAsync()
        {
            await ReleaseAsync();
            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            db.Libraries.Remove(await db.Libraries.SingleAsync(l => l.Id == LibraryId));
            await db.SaveChangesAsync();
            foreach (var path in _scratch) if (Directory.Exists(path)) Directory.Delete(path, true);
            Directory.Delete(Root, true);
            Client.Dispose();
        }
    }
}
