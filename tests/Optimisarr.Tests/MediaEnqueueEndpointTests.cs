using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Tests;

[Collection(TokenedApiCollection.Name)]
public sealed class MediaEnqueueEndpointTests(AdminTokenAuthEndpointTests.TokenedApi api)
{
    private HttpClient Admin()
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        return client;
    }

    private async Task<(int LibraryId, int MediaId, string Path)> SeedAsync(string videoCodec = "h264", MediaFileStatus status = MediaFileStatus.Probed)
    {
        var root = Path.Combine(api.LibraryDirectory, "enqueue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "tape.mkv");
        await File.WriteAllBytesAsync(path, new byte[1024]);
        var info = new FileInfo(path);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var library = new Library { Name = "Enqueue", Path = root, Priority = 3, AutoEnqueueEnabled = false, MinFileSizeBytes = 0, SkipEfficientSources = false };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        var file = new MediaFile
        {
            LibraryId = library.Id, Path = path, RelativePath = "tape.mkv", SizeBytes = info.Length,
            ModifiedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), Status = status,
            MediaKind = MediaKind.Video, Container = "matroska,webm", VideoCodec = videoCodec, PixelFormat = "yuv420p",
            Width = 1920, Height = 1080, DurationSeconds = 100, ProbedAt = DateTimeOffset.UtcNow,
        };
        db.MediaFiles.Add(file);
        await db.SaveChangesAsync();
        return (library.Id, file.Id, path);
    }

    private async Task<List<Job>> JobsAsync(int mediaId)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().Jobs.AsNoTracking()
            .Where(job => job.MediaFileId == mediaId).ToListAsync();
    }

    private async Task AddJobAsync(int mediaId, int libraryId, JobStatus status)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        db.Jobs.Add(new Job { MediaFileId = mediaId, LibraryId = libraryId, Status = status, Type = JobType.Normal, EnqueuedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task HoldQueueAsync(Func<Task> action)
    {
        using var client = Admin();
        (await client.PostAsync("/api/queue/pause", null)).EnsureSuccessStatusCode();
        try { await action(); }
        finally { (await client.PostAsync("/api/queue/resume", null)).EnsureSuccessStatusCode(); }
    }

    [Fact]
    public async Task Queueing_a_file_needs_no_automatic_enqueue_and_uses_the_librarys_priority() => await HoldQueueAsync(async () =>
    {
        var (_, mediaId, _) = await SeedAsync();
        using var client = Admin();

        using var response = await client.PostAsync($"/api/media/{mediaId}/enqueue", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("alreadyQueued").GetBoolean());
        var job = Assert.Single(await JobsAsync(mediaId));
        Assert.Equal(body.GetProperty("jobId").GetInt32(), job.Id);
        Assert.Equal(3, job.Priority);
        Assert.StartsWith("Queued manually", job.EnqueueReason);
    });

    [Fact]
    public async Task Many_requests_at_once_add_exactly_one_job() => await HoldQueueAsync(async () =>
    {
        var (_, mediaId, _) = await SeedAsync();
        using var client = Admin();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.PostAsync($"/api/media/{mediaId}/enqueue", null)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Single(await JobsAsync(mediaId));
        var flags = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<JsonElement>()));
        Assert.Single(flags, flag => !flag.GetProperty("alreadyQueued").GetBoolean());
    });

    [Fact]
    public async Task A_file_the_rules_skip_is_refused_with_their_reason()
    {
        var (_, mediaId, _) = await SeedAsync(videoCodec: "hevc");
        using var client = Admin();

        using var response = await client.PostAsync($"/api/media/{mediaId}/enqueue", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("media.enqueue.ineligible", await response.Content.ReadAsStringAsync());
        Assert.Empty(await JobsAsync(mediaId));
    }

    [Fact]
    public async Task An_excluded_file_is_refused()
    {
        var (_, mediaId, path) = await SeedAsync();
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            db.Exclusions.Add(new Exclusion { Path = path });
            await db.SaveChangesAsync();
        }
        using var client = Admin();

        using var response = await client.PostAsync($"/api/media/{mediaId}/enqueue", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Excluded", await response.Content.ReadAsStringAsync());
        Assert.Empty(await JobsAsync(mediaId));
    }

    [Fact]
    public async Task A_previously_failed_file_is_refused_so_it_is_retried_from_its_own_job()
    {
        var (libraryId, mediaId, _) = await SeedAsync();
        await AddJobAsync(mediaId, libraryId, JobStatus.Failed);
        using var client = Admin();

        using var response = await client.PostAsync($"/api/media/{mediaId}/enqueue", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Single(await JobsAsync(mediaId));
    }

    [Fact]
    public async Task An_unprobed_file_is_asked_to_be_probed_first()
    {
        var (_, mediaId, _) = await SeedAsync(status: MediaFileStatus.Discovered);
        using var client = Admin();

        using var response = await client.PostAsync($"/api/media/{mediaId}/enqueue", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("media.enqueue.needsProbe", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_file_changed_or_gone_since_it_was_scanned_is_refused(bool removed)
    {
        var (_, mediaId, path) = await SeedAsync();
        if (removed) File.Delete(path);
        else await File.WriteAllBytesAsync(path, new byte[2048]);
        using var client = Admin();

        using var response = await client.PostAsync($"/api/media/{mediaId}/enqueue", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("media.enqueue.needsProbe", await response.Content.ReadAsStringAsync());
        Assert.Empty(await JobsAsync(mediaId));
    }

    [Fact]
    public async Task An_unknown_file_is_not_found()
    {
        using var client = Admin();
        using var response = await client.PostAsync("/api/media/2147483647/enqueue", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Retrying_a_job_is_refused_while_the_same_file_has_another_active_job() => await HoldQueueAsync(async () =>
    {
        var (libraryId, mediaId, _) = await SeedAsync();
        await AddJobAsync(mediaId, libraryId, JobStatus.Failed);
        await AddJobAsync(mediaId, libraryId, JobStatus.Failed);
        var ids = (await JobsAsync(mediaId)).Select(job => job.Id).Order().ToArray();
        using var client = Admin();

        using var first = await client.PostAsync($"/api/jobs/{ids[0]}/retry", null);
        using var second = await client.PostAsync($"/api/jobs/{ids[1]}/retry", null);

        first.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("job.retry.alreadyActive", await second.Content.ReadAsStringAsync());
        Assert.Single(await JobsAsync(mediaId), job => job.Status == JobStatus.Queued);
    });
}
