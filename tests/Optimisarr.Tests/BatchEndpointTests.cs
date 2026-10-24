using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Tests;

/// <summary>
/// The bulk Queue and Library actions. Each one applies the rules of the single action it stands
/// in for — a batch may never be more permissive — and reports what happened to every selected
/// row, so a partial success is never silent.
/// </summary>
[Collection(TokenedApiCollection.Name)]
public sealed class BatchEndpointTests(AdminTokenAuthEndpointTests.TokenedApi api)
{
    [Fact]
    public async Task A_batch_remove_drops_the_failed_and_cancelled_jobs_and_reports_the_rest()
    {
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        var fixture = await SeedAsync(api, ("failed.mkv", JobStatus.Failed), ("cancelled.mkv", JobStatus.Cancelled), ("queued.mkv", JobStatus.Queued));
        try
        {
            using var response = await client.PostAsJsonAsync("/api/jobs/batch/remove", new { ids = fixture.Jobs });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(3, result.GetProperty("requested").GetInt32());

            var succeeded = result.GetProperty("succeeded").EnumerateArray().Select(id => id.GetInt32()).ToArray();
            Assert.Equal(2, succeeded.Length);
            Assert.Contains(fixture.Jobs[0], succeeded);
            Assert.Contains(fixture.Jobs[1], succeeded);

            var failures = result.GetProperty("failures").EnumerateArray().ToArray();
            Assert.Single(failures);
            Assert.Equal(fixture.Jobs[2], failures[0].GetProperty("id").GetInt32());
            Assert.Equal("job.remove.active", failures[0].GetProperty("code").GetString());

            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var left = await db.Jobs.AsNoTracking().Where(job => fixture.Jobs.Contains(job.Id)).Select(job => job.Id).ToListAsync();
            Assert.Equal([fixture.Jobs[2]], left);
        }
        finally
        {
            await CleanupAsync(api, fixture);
        }
    }

    [Fact]
    public async Task A_batch_remove_leaves_a_running_job_alone_unless_the_operator_asks_for_it_to_be_stopped()
    {
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        var fixture = await SeedAsync(api, ("running.mkv", JobStatus.Transcoding));
        try
        {
            using var refused = await client.PostAsJsonAsync("/api/jobs/batch/remove", new { ids = fixture.Jobs });
            var refusedResult = await refused.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Empty(refusedResult.GetProperty("succeeded").EnumerateArray());
            Assert.Equal("job.remove.active", refusedResult.GetProperty("failures")[0].GetProperty("code").GetString());

            using var stopped = await client.PostAsJsonAsync("/api/jobs/batch/remove", new { ids = fixture.Jobs, cancelActive = true });
            var stoppedResult = await stopped.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal([fixture.Jobs[0]], stoppedResult.GetProperty("succeeded").EnumerateArray().Select(id => id.GetInt32()));

            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            Assert.Empty(await db.Jobs.AsNoTracking().Where(job => fixture.Jobs.Contains(job.Id)).ToListAsync());
        }
        finally
        {
            await CleanupAsync(api, fixture);
        }
    }

    [Fact]
    public async Task A_batch_remove_never_takes_a_job_that_still_owns_a_rollback()
    {
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        var fixture = await SeedAsync(api, ("replaced.mkv", JobStatus.Failed));
        int replacementId;
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            db.Replacements.Add(new Replacement
            {
                JobId = fixture.Jobs[0],
                MediaFileId = fixture.MediaFiles[0],
                OriginalPath = fixture.Paths[0],
                FinalPath = fixture.Paths[0] + ".optimisarr",
                QuarantinePath = fixture.Paths[0] + ".quarantine",
                Status = ReplacementStatus.Replaced,
            });
            await db.SaveChangesAsync();
            replacementId = db.Replacements.Local.Single().Id;
        }
        try
        {
            using var response = await client.PostAsJsonAsync("/api/jobs/batch/remove", new { ids = fixture.Jobs });
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Empty(result.GetProperty("succeeded").EnumerateArray());
            Assert.Equal("job.recoveryPending", result.GetProperty("failures")[0].GetProperty("code").GetString());

            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            Assert.Single(await db.Jobs.AsNoTracking().Where(job => fixture.Jobs.Contains(job.Id)).ToListAsync());
            Assert.Single(await db.Replacements.AsNoTracking().Where(replacement => replacement.Id == replacementId).ToListAsync());
        }
        finally
        {
            await CleanupAsync(api, fixture);
        }
    }

    [Fact]
    public async Task A_batch_retry_queues_the_failed_jobs_again_and_refuses_the_others()
    {
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        var fixture = await SeedAsync(api, ("failed.mkv", JobStatus.Failed), ("ready.mkv", JobStatus.ReadyToReplace));
        try
        {
            using var response = await client.PostAsJsonAsync("/api/jobs/batch/retry", new { ids = fixture.Jobs });
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal([fixture.Jobs[0]], result.GetProperty("succeeded").EnumerateArray().Select(id => id.GetInt32()));
            Assert.Equal("job.retry.invalidState", result.GetProperty("failures")[0].GetProperty("code").GetString());

            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var retried = await db.Jobs.AsNoTracking().SingleAsync(job => job.Id == fixture.Jobs[0]);
            Assert.Equal(JobStatus.Queued, retried.Status);
        }
        finally
        {
            await CleanupAsync(api, fixture);
        }
    }

    [Fact]
    public async Task A_batch_retry_refuses_a_file_that_has_been_excluded()
    {
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        var fixture = await SeedAsync(api, ("failed.mkv", JobStatus.Failed));
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            db.Exclusions.Add(new Exclusion { Path = fixture.Paths[0], LibraryId = fixture.LibraryId, RelativePath = "failed.mkv" });
            await db.SaveChangesAsync();
        }
        try
        {
            using var response = await client.PostAsJsonAsync("/api/jobs/batch/retry", new { ids = fixture.Jobs });
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Empty(result.GetProperty("succeeded").EnumerateArray());
            Assert.Equal("job.retry.excluded", result.GetProperty("failures")[0].GetProperty("code").GetString());
        }
        finally
        {
            await CleanupAsync(api, fixture);
        }
    }

    [Fact]
    public async Task A_batch_exclude_records_the_exclusion_for_every_file_and_drops_their_failed_jobs()
    {
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        var fixture = await SeedAsync(api, ("one.mkv", JobStatus.Failed), ("two.mkv", JobStatus.Failed));
        try
        {
            using var response = await client.PostAsJsonAsync("/api/media/batch/exclude", new { mediaFileIds = fixture.MediaFiles });
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(2, result.GetProperty("succeeded").EnumerateArray().Count());
            Assert.Empty(result.GetProperty("failures").EnumerateArray());

            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var exclusions = await db.Exclusions.AsNoTracking()
                .Where(exclusion => exclusion.LibraryId == fixture.LibraryId)
                .ToListAsync();
            Assert.Equal(2, exclusions.Count);
            Assert.Empty(await db.Jobs.AsNoTracking().Where(job => fixture.Jobs.Contains(job.Id)).ToListAsync());
        }
        finally
        {
            await CleanupAsync(api, fixture);
        }
    }

    [Fact]
    public async Task A_batch_exclude_reports_a_file_it_cannot_find()
    {
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        var fixture = await SeedAsync(api, ("one.mkv", JobStatus.Failed));
        try
        {
            using var response = await client.PostAsJsonAsync("/api/media/batch/exclude", new { mediaFileIds = new[] { fixture.MediaFiles[0], 999_999 } });
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal([fixture.MediaFiles[0]], result.GetProperty("succeeded").EnumerateArray().Select(id => id.GetInt32()));
            Assert.Equal("media.notFound", result.GetProperty("failures")[0].GetProperty("code").GetString());
        }
        finally
        {
            await CleanupAsync(api, fixture);
        }
    }

    [Fact]
    public async Task A_batch_over_the_cap_is_refused_rather_than_partly_handled()
    {
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        var fixture = await SeedAsync(api, ("one.mkv", JobStatus.Failed));
        try
        {
            var ids = Enumerable.Range(1, 201).ToArray();
            using var response = await client.PostAsJsonAsync("/api/jobs/batch/remove", new { ids });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("batch.tooLarge", error.GetProperty("code").GetString());

            // The same batch within the cap is handled, so the refusal is about the size alone.
            using var accepted = await client.PostAsJsonAsync("/api/jobs/batch/remove", new { ids = ids.Take(200).ToArray() });
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }
        finally
        {
            await CleanupAsync(api, fixture);
        }
    }

    private sealed record Fixture(int LibraryId, int[] MediaFiles, int[] Jobs, string[] Paths);

    private static async Task<Fixture> SeedAsync(
        AdminTokenAuthEndpointTests.TokenedApi api,
        params (string Name, JobStatus Status)[] files)
    {
        var root = Path.Combine(api.LibraryDirectory, "batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var library = new Library { Name = "Batch fixtures", Path = root };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        var mediaIds = new List<int>();
        var jobIds = new List<int>();
        var paths = new List<string>();
        foreach (var (name, status) in files)
        {
            var path = Path.Combine(root, name);
            await File.WriteAllTextAsync(path, "fixture");
            var file = new MediaFile
            {
                LibraryId = library.Id,
                Path = path,
                RelativePath = name,
                Status = MediaFileStatus.Probed,
                MediaKind = MediaKind.Video,
                VideoCodec = "h264",
                Height = 1080,
                SizeBytes = 1_000,
                DurationSeconds = 60,
            };
            db.MediaFiles.Add(file);
            await db.SaveChangesAsync();
            mediaIds.Add(file.Id);
            paths.Add(path);
            var job = new Job { MediaFileId = file.Id, LibraryId = library.Id, Status = status, EnqueuedAt = DateTimeOffset.UtcNow };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            jobIds.Add(job.Id);
        }
        return new Fixture(library.Id, mediaIds.ToArray(), jobIds.ToArray(), paths.ToArray());
    }

    private static async Task CleanupAsync(AdminTokenAuthEndpointTests.TokenedApi api, Fixture fixture)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var library = await db.Libraries.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == fixture.LibraryId);
        if (library is not null)
        {
            // The replacement references the job and the file, so it goes first.
            db.Replacements.RemoveRange(await db.Replacements.Where(replacement => fixture.Jobs.Contains(replacement.JobId)).ToListAsync());
            db.Jobs.RemoveRange(await db.Jobs.Where(job => fixture.Jobs.Contains(job.Id)).ToListAsync());
            db.Exclusions.RemoveRange(await db.Exclusions.Where(exclusion => exclusion.LibraryId == fixture.LibraryId).ToListAsync());
            db.MediaFiles.RemoveRange(await db.MediaFiles.Where(file => fixture.MediaFiles.Contains(file.Id)).ToListAsync());
            db.Libraries.Remove(library);
            await db.SaveChangesAsync();
            if (Directory.Exists(library.Path)) Directory.Delete(library.Path, true);
        }
    }
}
