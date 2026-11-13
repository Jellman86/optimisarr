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
/// The plan is what "Optimise this file…" shows before anything is queued, so it must say the
/// same as the queue would do and must change nothing.
/// </summary>
[Collection(TokenedApiCollection.Name)]
public sealed class MediaPlanEndpointTests(AdminTokenAuthEndpointTests.TokenedApi api)
{
    [Fact]
    public async Task Plan_says_what_would_happen_to_an_eligible_file_and_queues_nothing()
    {
        var (_, mediaId) = await SeedAsync(library =>
        {
            library.KeepAudioLanguages = "eng";
            library.TargetContainer = "source";
        });
        using var client = Admin();

        var plan = await client.GetFromJsonAsync<JsonElement>($"/api/media/{mediaId}/plan");

        Assert.True(plan.GetProperty("eligible").GetBoolean());
        var detail = plan;
        Assert.Equal("Video", detail.GetProperty("kind").GetString());
        Assert.Equal("Encode", detail.GetProperty("action").GetString());
        Assert.Equal("h264", detail.GetProperty("source").GetProperty("videoCodec").GetString());
        Assert.Equal(1080, detail.GetProperty("source").GetProperty("height").GetInt32());
        Assert.Equal("hevc", detail.GetProperty("picture").GetProperty("codec").GetString());
        Assert.Equal("Off", detail.GetProperty("picture").GetProperty("noiseReduction").GetString());
        Assert.Equal("aac", detail.GetProperty("sound").GetProperty("codec").GetString());
        var audio = detail.GetProperty("audioTracks").EnumerateArray().ToList();
        Assert.True(audio[0].GetProperty("kept").GetBoolean());
        Assert.False(audio[1].GetProperty("kept").GetBoolean());
        Assert.Equal("fra", audio[1].GetProperty("language").GetString());
        Assert.False(detail.GetProperty("file").GetProperty("renamed").GetBoolean());
        Assert.Equal("tape.mkv", detail.GetProperty("file").GetProperty("fileName").GetString());
        Assert.Empty(await JobsAsync(mediaId));
    }

    [Fact]
    public async Task Plan_uses_the_inventorys_audio_codecs_to_say_an_mp4_library_writes_matroska()
    {
        var (_, mediaId) = await SeedAsync(library =>
        {
            library.TargetContainer = "mp4";
            library.VideoAudioCodec = "copy";
        }, audioCodecs: "truehd");
        using var client = Admin();

        var file = (await client.GetFromJsonAsync<JsonElement>($"/api/media/{mediaId}/plan")).GetProperty("file");

        Assert.Equal("mkv", file.GetProperty("container").GetString());
        Assert.Equal("tape.mkv", file.GetProperty("fileName").GetString());
        Assert.False(file.GetProperty("mayBecomeMatroska").GetBoolean());
    }

    [Fact]
    public async Task Plan_gives_the_reason_a_file_is_skipped()
    {
        var (_, mediaId) = await SeedAsync(videoCodec: "hevc");
        using var client = Admin();

        var plan = await client.GetFromJsonAsync<JsonElement>($"/api/media/{mediaId}/plan");

        Assert.False(plan.GetProperty("eligible").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(plan.GetProperty("reason").GetString()));
    }

    [Fact]
    public async Task Plan_needs_a_probed_file_and_a_file_that_exists()
    {
        var (_, unprobed) = await SeedAsync(status: MediaFileStatus.Discovered);
        using var client = Admin();

        var needsProbe = await client.GetAsync($"/api/media/{unprobed}/plan");
        var missing = await client.GetAsync("/api/media/2147483000/plan");

        Assert.Equal(HttpStatusCode.Conflict, needsProbe.StatusCode);
        Assert.Equal("media.enqueue.needsProbe", (await needsProbe.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Plan_requires_the_admin_token()
    {
        using var anonymous = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/media/1/plan")).StatusCode);
    }

    [Fact]
    public async Task The_dry_run_unlock_preview_counts_what_would_start_without_being_asked()
    {
        using var client = Admin();
        var before = await client.GetFromJsonAsync<JsonElement>("/api/settings/dry-run-unlock");
        var (replacing, replacingMedia) = await SeedAsync(library =>
        {
            library.MediaType = MediaType.Film;
            library.AutoReplace = true;
            library.AutoDedupeEnabled = true;
        });
        // Saving elsewhere wins over an old "replace automatically", so this job would not be replaced.
        var (elsewhere, elsewhereMedia) = await SeedAsync(library =>
        {
            library.AutoReplace = true;
            library.MoveOnComplete = true;
            library.TargetFolder = Path.Combine(api.LibraryDirectory, "..", "elsewhere");
        });
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        db.Jobs.AddRange(
            new Job { MediaFileId = replacingMedia, LibraryId = replacing, Status = JobStatus.ReadyToReplace, VerificationPassed = true },
            new Job { MediaFileId = elsewhereMedia, LibraryId = elsewhere, Status = JobStatus.ReadyToReplace, VerificationPassed = true });
        await db.SaveChangesAsync();
        try
        {
            var after = await client.GetFromJsonAsync<JsonElement>("/api/settings/dry-run-unlock");

            Assert.Equal(before.GetProperty("jobsToReplace").GetInt32() + 1, after.GetProperty("jobsToReplace").GetInt32());
            Assert.Equal(
                before.GetProperty("librariesCleaningDuplicatesDaily").GetInt32() + 1,
                after.GetProperty("librariesCleaningDuplicatesDaily").GetInt32());
            Assert.True(after.GetProperty("needsConfirmation").GetBoolean());
            Assert.True(after.TryGetProperty("originalsToDelete", out _));
            Assert.True(after.TryGetProperty("duplicatesToDelete", out _));
        }
        finally
        {
            // The fixture's database is shared: leave no job another test could try to replace.
            await db.Jobs.Where(job => job.MediaFileId == replacingMedia || job.MediaFileId == elsewhereMedia).ExecuteDeleteAsync();
            await db.Libraries.Where(library => library.Id == replacing).ExecuteUpdateAsync(set => set.SetProperty(l => l.AutoDedupeEnabled, false));
        }
    }

    private HttpClient Admin()
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        return client;
    }

    private async Task<(int LibraryId, int MediaId)> SeedAsync(
        Action<Library>? configure = null,
        string videoCodec = "h264",
        MediaFileStatus status = MediaFileStatus.Probed,
        string audioCodecs = "aac")
    {
        var root = Path.Combine(api.LibraryDirectory, "plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "tape.mkv");
        await File.WriteAllBytesAsync(path, new byte[1024]);
        var info = new FileInfo(path);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var library = new Library { Name = "Plan " + Guid.NewGuid().ToString("N"), Path = root, AutoEnqueueEnabled = false, MinFileSizeBytes = 0, SkipEfficientSources = false };
        configure?.Invoke(library);
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        var file = new MediaFile
        {
            LibraryId = library.Id, Path = path, RelativePath = "tape.mkv", SizeBytes = info.Length,
            ModifiedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), Status = status,
            MediaKind = MediaKind.Video, Container = "matroska,webm", VideoCodec = videoCodec, PixelFormat = "yuv420p",
            Width = 1920, Height = 1080, DurationSeconds = 100, AudioLanguages = "eng, fra", AudioTrackCount = 2,
            SubtitleLanguages = "eng", SubtitleTrackCount = 1, MaxAudioChannels = 6, ProbedAt = DateTimeOffset.UtcNow,
            AudioCodecs = audioCodecs,
        };
        db.MediaFiles.Add(file);
        await db.SaveChangesAsync();
        return (library.Id, file.Id);
    }

    private async Task<List<Job>> JobsAsync(int mediaId)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().Jobs.AsNoTracking()
            .Where(job => job.MediaFileId == mediaId).ToListAsync();
    }
}
