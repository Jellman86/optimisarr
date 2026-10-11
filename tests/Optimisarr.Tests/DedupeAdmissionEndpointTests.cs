using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Api.Deduplication;
using Optimisarr.Core.Deduplication;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Tests;

[Collection(TokenedApiCollection.Name)]
public sealed class DedupeAdmissionEndpointTests(AdminTokenAuthEndpointTests.TokenedApi api)
{
    [Fact]
    public async Task Reserved_files_refuse_preview_retry_and_calibration_and_enqueue_reports_them_ineligible()
    {
        var root = Path.Combine(api.LibraryDirectory, "admission-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        Guid actionId = Guid.NewGuid();
        int libraryId, mediaId, jobId;
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var library = new Library { Name = "Admission fixtures", Path = root };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            var files = new[] { "keeper.mkv", "copy.mkv" }.Select(name => new MediaFile { LibraryId = library.Id,
                Path = Path.Combine(root, name), RelativePath = name, Status = MediaFileStatus.Probed,
                MediaKind = MediaKind.Video, VideoCodec = "h264", Height = 1080, SizeBytes = 1_000_000_000, DurationSeconds = 60 }).ToArray();
            db.MediaFiles.AddRange(files);
            await db.SaveChangesAsync();
            var job = new Job { MediaFileId = files[0].Id, LibraryId = library.Id, Status = JobStatus.Failed };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            libraryId = library.Id; mediaId = files[0].Id; jobId = job.Id;
            var plan = new DedupePreparation(actionId, library.Id, new('a', 64), new('b', 64), files.Select((f, i) =>
                new DedupeFileSnapshot(f.Id, library.Id, root, f.Path, i == 0 ? null : Path.Combine(root, "quarantine", "copy"),
                    $"disk:{i}", 42, 1, new('c', 64), i == 0)).ToArray());
            Assert.Equal(DedupePreparationResult.Created, await api.Services.GetRequiredService<DedupeJournalStore>().TryPrepareAsync(plan, default));
        }
        try
        {
            foreach (var route in new[] { $"/api/media/{mediaId}/preview", $"/api/jobs/{jobId}/retry" })
            {
                using var response = await client.PostAsync(route, null);
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                var error = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("dedupe.reserved", error.GetProperty("code").GetString());
                Assert.Contains("duplicate", error.GetProperty("error").GetString());
            }
            using var calibration = await client.PostAsJsonAsync($"/api/libraries/{libraryId}/calibration", new { mediaFileId = mediaId });
            Assert.Equal(HttpStatusCode.BadRequest, calibration.StatusCode);
            Assert.Contains("duplicate", await calibration.Content.ReadAsStringAsync());
            using var enqueue = await client.PostAsync($"/api/libraries/{libraryId}/enqueue", null);
            enqueue.EnsureSuccessStatusCode();
            var result = await enqueue.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(0, result.GetProperty("enqueued").GetInt32());
            Assert.Equal(2, result.GetProperty("ineligible").GetInt32());
            using var check = api.Services.CreateScope();
            var db = check.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            Assert.Equal(JobStatus.Failed, (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId)).Status);
            Assert.Equal(1, await db.Jobs.CountAsync(j => j.LibraryId == libraryId));
        }
        finally
        {
            await api.Services.GetRequiredService<DedupeJournalStore>().CancelPreparedAsync(actionId, default);
            using var cleanup = api.Services.CreateScope();
            var db = cleanup.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            db.Libraries.Remove(await db.Libraries.SingleAsync(l => l.Id == libraryId));
            await db.SaveChangesAsync();
            Directory.Delete(root, true);
        }
    }
}
