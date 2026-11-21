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
/// Before pressing Replace or Accept anyway, the user compares the original with the output that
/// would take its place. Only an output one of those could put in place is served.
/// </summary>
[Collection(TokenedApiCollection.Name)]
public sealed class JobOutputContentEndpointTests(AdminTokenAuthEndpointTests.TokenedApi api)
{
    [Fact]
    public async Task A_job_ready_to_replace_streams_its_verified_output()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var (jobId, mediaId) = await SeedAsync(JobStatus.ReadyToReplace, JobType.Normal, bytes);
        using var client = Admin();
        try
        {
            using var response = await client.GetAsync($"/api/jobs/{jobId}/output/content");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
            Assert.Equal("bytes", response.Headers.AcceptRanges.Single());
        }
        finally { await CleanUpAsync(mediaId); }
    }

    [Theory]
    [InlineData(JobStatus.Failed, JobType.Normal)]
    [InlineData(JobStatus.Transcoding, JobType.Normal)]
    [InlineData(JobStatus.ReadyToReplace, JobType.Preview)]
    public async Task Any_other_output_is_not_served(JobStatus status, JobType type)
    {
        var (jobId, mediaId) = await SeedAsync(status, type, [9, 9, 9]);
        using var client = Admin();
        try
        {
            using var response = await client.GetAsync($"/api/jobs/{jobId}/output/content");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally { await CleanUpAsync(mediaId); }
    }

    [Fact]
    public async Task A_ready_job_whose_verification_did_not_pass_is_not_served()
    {
        var (jobId, mediaId) = await SeedAsync(JobStatus.ReadyToReplace, JobType.Normal, [9], verificationPassed: false);
        using var client = Admin();
        try
        {
            using var response = await client.GetAsync($"/api/jobs/{jobId}/output/content");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally { await CleanUpAsync(mediaId); }
    }

    [Fact]
    public async Task A_failed_job_that_only_missed_its_picture_target_streams_its_output_and_says_it_can_be_accepted()
    {
        var bytes = new byte[] { 4, 2 };
        var (jobId, mediaId) = await SeedAsync(JobStatus.Failed, JobType.Normal, bytes, verificationPassed: false, belowTarget: true);
        using var client = Admin();
        try
        {
            using var response = await client.GetAsync($"/api/jobs/{jobId}/output/content");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());

            var jobs = await client.GetFromJsonAsync<JsonElement>("/api/jobs");
            var belowTarget = jobs.EnumerateArray().Single(j => j.GetProperty("id").GetInt32() == jobId).GetProperty("belowTarget");
            Assert.True(belowTarget.GetProperty("eligible").GetBoolean());
            var shortfall = belowTarget.GetProperty("shortfalls").EnumerateArray().Single();
            Assert.Equal("HarmonicMean", shortfall.GetProperty("target").GetString());
            Assert.Equal(94, shortfall.GetProperty("score").GetDouble());
            Assert.Equal(95, shortfall.GetProperty("minimum").GetDouble());
        }
        finally { await CleanUpAsync(mediaId); }
    }

    [Fact]
    public async Task The_output_needs_the_admin_token()
    {
        var (jobId, mediaId) = await SeedAsync(JobStatus.ReadyToReplace, JobType.Normal, [1]);
        using var client = api.CreateClient();
        try
        {
            using var response = await client.GetAsync($"/api/jobs/{jobId}/output/content");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally { await CleanUpAsync(mediaId); }
    }

    [Fact]
    public async Task A_queued_job_says_what_kind_of_media_it_is()
    {
        var (jobId, mediaId) = await SeedAsync(JobStatus.ReadyToReplace, JobType.Normal, [1]);
        using var client = Admin();
        try
        {
            var jobs = await client.GetFromJsonAsync<JsonElement>("/api/jobs");
            var job = jobs.EnumerateArray().Single(j => j.GetProperty("id").GetInt32() == jobId);

            Assert.Equal("Video", job.GetProperty("mediaKind").GetString());
        }
        finally { await CleanUpAsync(mediaId); }
    }

    private HttpClient Admin()
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        return client;
    }

    private async Task<(int JobId, int MediaId)> SeedAsync(
        JobStatus status, JobType type, byte[] output, bool? verificationPassed = null, bool belowTarget = false)
    {
        var root = Path.Combine(api.LibraryDirectory, "output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "tape.mkv");
        await File.WriteAllBytesAsync(source, new byte[64]);
        var outputPath = Path.Combine(root, "tape.optimised.mkv");
        await File.WriteAllBytesAsync(outputPath, output);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var library = new Library { Name = "Output " + Guid.NewGuid().ToString("N"), Path = root, AutoEnqueueEnabled = false };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        var file = new MediaFile
        {
            LibraryId = library.Id, Path = source, RelativePath = "tape.mkv", SizeBytes = 64,
            ModifiedAt = DateTimeOffset.UtcNow, Status = MediaFileStatus.Probed, MediaKind = MediaKind.Video,
        };
        db.MediaFiles.Add(file);
        await db.SaveChangesAsync();
        var job = new Job
        {
            MediaFileId = file.Id, LibraryId = library.Id, Status = status, Type = type,
            WorkOutputPath = outputPath, VerificationPassed = verificationPassed ?? status == JobStatus.ReadyToReplace,
        };
        if (belowTarget)
        {
            job.VerifiedSourceSha256 = new string('a', 64);
            job.VerifiedOutputSha256 = new string('b', 64);
            job.VerificationReportJson = JsonSerializer.Serialize(new Optimisarr.Core.Verification.VerificationReport(
                [new("Perceptual quality (VMAF)", Optimisarr.Core.Verification.CheckOutcome.Failed, "94 under 95")],
                Context: new("libx265", 24, 24, "Crf", 1, "Full file", 95, 90, 40),
                Vmaf: new(true, null, new(95, 94, 70, null, null, VmafFifthPercentile: 91))),
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        }
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return (job.Id, file.Id);
    }

    private async Task CleanUpAsync(int mediaId)
    {
        // The fixture's database is shared: leave no job another test could try to replace.
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await db.Jobs.Where(job => job.MediaFileId == mediaId).ExecuteDeleteAsync();
        var file = await db.MediaFiles.AsNoTracking().SingleAsync(f => f.Id == mediaId);
        await db.MediaFiles.Where(f => f.Id == mediaId).ExecuteDeleteAsync();
        await db.Libraries.Where(l => l.Id == file.LibraryId).ExecuteDeleteAsync();
        Directory.Delete(Path.GetDirectoryName(file.Path)!, recursive: true);
    }
}
