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
/// The bulk actions of the Excluded tab and the Inventory page. Lifting an exclusion only forgets
/// the decision — the file itself stays on disk — and a re-probe batch reports what became of
/// every file it was asked to inspect, so a partial success is never silent.
/// </summary>
[Collection(TokenedApiCollection.Name)]
public sealed class MediaBatchEndpointTests(AdminTokenAuthEndpointTests.TokenedApi api)
{
    [Fact]
    public async Task A_batch_remove_lifts_several_exclusions_at_once_and_leaves_the_files_on_disk()
    {
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        var fixture = await SeedAsync(api, "one.mkv", "two.mkv");
        try
        {
            using var response = await client.PostAsJsonAsync("/api/exclusions/batch/remove", new { ids = fixture.Exclusions });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(2, result.GetProperty("requested").GetInt32());
            Assert.Equal(fixture.Exclusions.OrderBy(id => id), result.GetProperty("succeeded").EnumerateArray().Select(id => id.GetInt32()).OrderBy(id => id));
            Assert.Empty(result.GetProperty("failures").EnumerateArray());

            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            Assert.Empty(await db.Exclusions.AsNoTracking().Where(exclusion => fixture.Exclusions.Contains(exclusion.Id)).ToListAsync());
            // The media files themselves are untouched: only the decision to skip them is forgotten.
            Assert.Equal(2, await db.MediaFiles.AsNoTracking().Where(file => fixture.MediaFiles.Contains(file.Id)).CountAsync());
            foreach (var path in fixture.Paths)
            {
                Assert.True(File.Exists(path), $"The original file {path} must stay on disk.");
            }
        }
        finally
        {
            await CleanupAsync(api, fixture);
        }
    }

    [Fact]
    public async Task A_batch_remove_reports_an_exclusion_it_cannot_find_and_still_lifts_the_others()
    {
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        var fixture = await SeedAsync(api, "one.mkv");
        try
        {
            using var response = await client.PostAsJsonAsync("/api/exclusions/batch/remove", new { ids = new[] { fixture.Exclusions[0], 999_999 } });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal([fixture.Exclusions[0]], result.GetProperty("succeeded").EnumerateArray().Select(id => id.GetInt32()));
            var failures = result.GetProperty("failures").EnumerateArray().ToArray();
            Assert.Single(failures);
            Assert.Equal(999_999, failures[0].GetProperty("id").GetInt32());
            Assert.Equal("exclusion.notFound", failures[0].GetProperty("code").GetString());
        }
        finally
        {
            await CleanupAsync(api, fixture);
        }
    }

    [Fact]
    public async Task A_batch_remove_counts_a_repeated_id_once()
    {
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        var fixture = await SeedAsync(api, "one.mkv");
        try
        {
            using var response = await client.PostAsJsonAsync("/api/exclusions/batch/remove", new { ids = new[] { fixture.Exclusions[0], fixture.Exclusions[0] } });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, result.GetProperty("requested").GetInt32());
            Assert.Single(result.GetProperty("succeeded").EnumerateArray());
            Assert.Empty(result.GetProperty("failures").EnumerateArray());
        }
        finally
        {
            await CleanupAsync(api, fixture);
        }
    }

    [Fact]
    public async Task A_batch_remove_over_the_cap_is_refused_rather_than_partly_handled()
    {
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        var fixture = await SeedAsync(api, "one.mkv");
        try
        {
            var ids = Enumerable.Range(1, 201).ToArray();
            using var response = await client.PostAsJsonAsync("/api/exclusions/batch/remove", new { ids });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("batch.tooLarge", error.GetProperty("code").GetString());

            // The same batch within the cap is handled, so the refusal is about the size alone.
            using var accepted = await client.PostAsJsonAsync("/api/exclusions/batch/remove", new { ids = ids.Take(200).ToArray() });
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }
        finally
        {
            await CleanupAsync(api, fixture);
        }
    }

    [Fact]
    public async Task A_probe_batch_inspects_every_file_and_reports_what_became_of_each()
    {
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        var fixture = await SeedAsync(api, "one.mkv", "two.mkv");
        try
        {
            using var response = await client.PostAsJsonAsync("/api/media/batch/probe", new { ids = new[] { fixture.MediaFiles[0], fixture.MediaFiles[1], 999_999 } });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var started = await response.Content.ReadFromJsonAsync<JsonElement>();
            var batchId = started.GetProperty("id").GetInt32();
            Assert.Equal(3, started.GetProperty("total").GetInt32());

            var finished = await WaitForBatchAsync(client, batchId);
            Assert.Equal("Completed", finished.GetProperty("state").GetString());
            Assert.Equal(3, finished.GetProperty("completed").GetInt32());
            Assert.Equal(2, finished.GetProperty("succeeded").GetInt32());
            Assert.Equal(1, finished.GetProperty("skipped").GetInt32());
            Assert.Equal(0, finished.GetProperty("failed").GetInt32());

            var items = finished.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(3, items.Length);
            Assert.Contains(items, item => item.GetProperty("mediaFileId").GetInt32() == fixture.MediaFiles[0] && item.GetProperty("outcome").GetString() == "Succeeded");
            Assert.Contains(items, item => item.GetProperty("mediaFileId").GetInt32() == 999_999 && item.GetProperty("outcome").GetString() == "Skipped");

            // The batch reuses the single-file probe, so the file now carries what the probe found.
            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var probed = await db.MediaFiles.AsNoTracking().SingleAsync(file => file.Id == fixture.MediaFiles[0]);
            Assert.Equal(MediaFileStatus.Probed, probed.Status);
            Assert.Equal(1920, probed.Width);
        }
        finally
        {
            await CleanupAsync(api, fixture);
        }
    }

    [Fact]
    public async Task A_probe_batch_over_the_cap_is_refused_rather_than_partly_handled()
    {
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        var fixture = await SeedAsync(api, "one.mkv");
        try
        {
            var ids = Enumerable.Range(1, 201).ToArray();
            using var response = await client.PostAsJsonAsync("/api/media/batch/probe", new { ids });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("batch.tooLarge", error.GetProperty("code").GetString());
        }
        finally
        {
            await CleanupAsync(api, fixture);
        }
    }

    [Fact]
    public async Task A_probe_batch_that_is_not_there_is_reported_as_missing()
    {
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        using var response = await client.GetAsync("/api/media/batch/probe/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("batch.notFound", error.GetProperty("code").GetString());
    }

    private static async Task<JsonElement> WaitForBatchAsync(HttpClient client, int batchId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var response = await client.GetAsync($"/api/media/batch/probe/{batchId}");
            response.EnsureSuccessStatusCode();
            var batch = await response.Content.ReadFromJsonAsync<JsonElement>();
            var state = batch.GetProperty("state").GetString();
            if (state is "Completed" or "Cancelled" or "Failed")
            {
                return batch;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Probe batch {batchId} did not finish in time.");
    }

    private sealed record Fixture(int LibraryId, int[] MediaFiles, int[] Exclusions, string[] Paths);

    private static async Task<Fixture> SeedAsync(AdminTokenAuthEndpointTests.TokenedApi api, params string[] names)
    {
        var root = Path.Combine(api.LibraryDirectory, "media-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var library = new Library { Name = "Media batch fixtures", Path = root };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        var mediaIds = new List<int>();
        var exclusionIds = new List<int>();
        var paths = new List<string>();
        foreach (var name in names)
        {
            var path = Path.Combine(root, name);
            await File.WriteAllTextAsync(path, "fixture");
            var file = new MediaFile
            {
                LibraryId = library.Id,
                Path = path,
                RelativePath = name,
                Status = MediaFileStatus.Discovered,
                MediaKind = MediaKind.Video,
                SizeBytes = 1_000,
            };
            db.MediaFiles.Add(file);
            await db.SaveChangesAsync();
            mediaIds.Add(file.Id);
            paths.Add(path);
            var exclusion = new Exclusion { Path = path, LibraryId = library.Id, RelativePath = name, Source = ExclusionSource.Manual };
            db.Exclusions.Add(exclusion);
            await db.SaveChangesAsync();
            exclusionIds.Add(exclusion.Id);
        }

        return new Fixture(library.Id, mediaIds.ToArray(), exclusionIds.ToArray(), paths.ToArray());
    }

    private static async Task CleanupAsync(AdminTokenAuthEndpointTests.TokenedApi api, Fixture fixture)
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var library = await db.Libraries.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == fixture.LibraryId);
        if (library is not null)
        {
            db.Exclusions.RemoveRange(await db.Exclusions.Where(exclusion => fixture.Exclusions.Contains(exclusion.Id)).ToListAsync());
            db.MediaFiles.RemoveRange(await db.MediaFiles.Where(file => fixture.MediaFiles.Contains(file.Id)).ToListAsync());
            db.Libraries.Remove(library);
            await db.SaveChangesAsync();
            if (Directory.Exists(library.Path)) Directory.Delete(library.Path, true);
        }
    }
}
