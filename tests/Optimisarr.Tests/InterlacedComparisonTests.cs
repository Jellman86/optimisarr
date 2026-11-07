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
public sealed class InterlacedComparisonTests(AdminTokenAuthEndpointTests.TokenedApi api)
{
    private HttpClient Admin()
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        return client;
    }

    private async Task<(int LibraryId, int MediaId)> SeedAsync(string fileName, string? fieldOrder, RuleProfile profile = RuleProfile.ConservativeHevc)
    {
        var root = Path.Combine(api.LibraryDirectory, "interlace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, fileName);
        await File.WriteAllBytesAsync(path, new byte[1024]);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var library = new Library { Name = "Interlace", Path = root, RuleProfile = profile, AutoEnqueueEnabled = false, HdrHandling = HdrHandling.TonemapToSdr };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        var file = new MediaFile
        {
            LibraryId = library.Id, Path = path, RelativePath = fileName, SizeBytes = 1024, ModifiedAt = DateTimeOffset.UtcNow,
            Status = MediaFileStatus.Probed, MediaKind = MediaKind.Video, Container = "matroska,webm", VideoCodec = "h264",
            PixelFormat = "yuv420p", BitsPerRawSample = 8, Width = 1920, Height = 1080, DurationSeconds = 1200,
            ProbedAt = DateTimeOffset.UtcNow, FieldOrder = fieldOrder,
        };
        db.MediaFiles.Add(file);
        await db.SaveChangesAsync();
        return (library.Id, file.Id);
    }

    private async Task<MediaFile> MediaAsync(int mediaId)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().MediaFiles.AsNoTracking().SingleAsync(file => file.Id == mediaId);
    }

    [Theory]
    [InlineData("tape.mkv", "tt")]
    [InlineData("interlaced tape.mkv", null)]
    public async Task Calibration_refuses_an_interlaced_source_and_creates_no_jobs(string fileName, string? recorded)
    {
        var (libraryId, mediaId) = await SeedAsync(fileName, recorded);
        using var client = Admin();

        using var response = await client.PostAsJsonAsync($"/api/libraries/{libraryId}/calibration",
            new { mediaFileId = mediaId, diagnosticsEnabled = false, ignoreActiveStreams = true, compareVideoPresets = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Interlaced", await response.Content.ReadAsStringAsync());
        Assert.Equal("tt", (await MediaAsync(mediaId)).FieldOrder);
        using var scope = api.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().Jobs.AnyAsync(job => job.MediaFileId == mediaId));
    }

    private static async Task<bool> SourceInterlacedAsync(HttpClient client, int jobId) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/preview/{jobId}")).GetProperty("sourceInterlaced").GetBoolean();

    private static async Task<int> PreviewAsync(HttpClient client, int mediaId)
    {
        using var created = await client.PostAsync($"/api/media/{mediaId}/preview", null);
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetInt32();
    }

    [Theory]
    [InlineData(RuleProfile.ConservativeHevc, true)]
    [InlineData(RuleProfile.TrackCleanup, false)]
    public async Task The_preview_warning_is_only_for_a_re_encode_a_real_job_would_refuse(RuleProfile profile, bool warned)
    {
        var (_, mediaId) = await SeedAsync("tape.mkv", "tt", profile);
        using var client = Admin();

        var jobId = await PreviewAsync(client, mediaId);

        Assert.Equal(warned, await SourceInterlacedAsync(client, jobId));
    }
}
