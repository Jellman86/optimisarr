using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Data;

namespace Optimisarr.Tests;

[Collection(TokenedApiCollection.Name)]
public sealed class PreviewQualitySearchEndpointTests(AdminTokenAuthEndpointTests.TokenedApi api)
{
    [Theory]
    [InlineData(null, new string[0])]
    [InlineData(SkippedQualitySearch.SoundtrackBitrates | SkippedQualitySearch.VideoQuality, new[] { "SoundtrackBitrates", "VideoQuality" })]
    public async Task A_preview_reports_the_quality_searches_its_attempt_left_out(SkippedQualitySearch? recorded, string[] expected)
    {
        int libraryId, jobId;
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var library = new Library { Name = "Preview search", Path = Path.Combine(api.LibraryDirectory, "preview-search-" + Guid.NewGuid()) };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            var media = new MediaFile
            {
                LibraryId = library.Id, Path = Path.Combine(library.Path, "film.mkv"), RelativePath = "film.mkv",
                Status = MediaFileStatus.Probed, MediaKind = MediaKind.Video, SizeBytes = 42, DurationSeconds = 300, VideoCodec = "h264",
            };
            db.MediaFiles.Add(media);
            await db.SaveChangesAsync();
            var job = new Job
            {
                MediaFileId = media.Id, LibraryId = library.Id, Type = JobType.Preview, Status = JobStatus.Queued,
                SkippedQualitySearches = recorded,
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            (libraryId, jobId) = (library.Id, job.Id);
        }

        try
        {
            using var client = api.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
            var preview = await client.GetFromJsonAsync<JsonElement>($"/api/preview/{jobId}");

            Assert.Equal(expected, preview.GetProperty("skippedQualitySearches").EnumerateArray().Select(name => name.GetString()));
        }
        finally
        {
            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            await db.Jobs.Where(job => job.Id == jobId).ExecuteDeleteAsync();
            await db.MediaFiles.Where(media => media.LibraryId == libraryId).ExecuteDeleteAsync();
            await db.Libraries.Where(library => library.Id == libraryId).ExecuteDeleteAsync();
        }
    }
}
