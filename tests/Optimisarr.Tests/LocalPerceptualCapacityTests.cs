using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Api.Queue;
using Optimisarr.Api.Replacement;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Tests;

[Collection(TokenedApiCollection.Name)]
public sealed class LocalPerceptualCapacityTests(AdminTokenAuthEndpointTests.TokenedApi api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() } };

    [Theory]
    [InlineData("memory", false)]
    [InlineData("memory", true)]
    [InlineData("unknown", true)]
    [InlineData("ram-workspace", true)]
    [InlineData("disk-workspace", true)]
    public async Task Local_capacity_refusal_keeps_the_job_queued_and_preserves_retry_intent_without_spending_media_budgets(string shortage, bool retry)
    {
        var path = Path.Combine(api.LibraryDirectory, "capacity-" + Guid.NewGuid().ToString("N") + ".png");
        await File.WriteAllTextAsync(path, "owned synthetic source");
        var sourceHash = await FileContentIdentity.HashAsync(path, default);
        var size = new FileInfo(path).Length;
        var policy = VerificationPolicy.Default with { RequireSizeReduction = false,
            ImagePerceptualGateEnabled = true, MinimumImagePerceptualScore = 85,
            ImageQualityGateEnabled = false, ImageMetadataGateEnabled = false };
        var retained = retry ? new PerceptualSearchSummary(MediaKind.Image, "libwebp", sourceHash, 85, null, [],
            new([new(80, 1000, new(true, true, 90, null)), new(60, 800, new(true, true, 86, null))],
                new(null, 60, false, "measured")), RetryValue: 70) : null;
        int libraryId;
        int jobId;
        DbContextOptions<OptimisarrDbContext> options;
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>(); options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(db.Database.GetConnectionString()).Options;
            var library = new Library { Name = "Capacity fixture", Path = api.LibraryDirectory, MediaType = MediaType.Photo };
            db.Libraries.Add(library); await db.SaveChangesAsync(); libraryId = library.Id;
            var media = new MediaFile { LibraryId = libraryId, Path = path, RelativePath = Path.GetFileName(path), FailureCount = 2,
                SizeBytes = size, MediaKind = MediaKind.Image };
            db.MediaFiles.Add(media); await db.SaveChangesAsync();
            var job = new Job { LibraryId = libraryId, MediaFileId = media.Id, ExecutionAttempt = 3, Attempt = 3, Status = JobStatus.Transcoding,
                SourceSha256 = sourceHash, QualityRetryCount = retry ? 1 : 0, RetryReason = retry ? "HigherPerceptualQualityRetry" : null,
                PerceptualSearchJson = retained is null ? null : JsonSerializer.Serialize(retained, Json) };
            db.Jobs.Add(job); await db.SaveChangesAsync(); jobId = job.Id;
        }
        var reads = 0;
        var services = new ServiceCollection();
        services.AddScoped(_ => new OptimisarrDbContext(options));
        services.AddSingleton(new ImagePerceptualObservationService((_, _, _) =>
        {
            reads++;
            return Task.FromResult(new ImagePerceptualReport(new(32, 32, false, 0, 100, [100]), null,
                sourceHash, sourceHash, new('c', 64), 1));
        }));
        await using var isolated = services.BuildServiceProvider();
        var memory = shortage == "unknown" ? (long?)null : PerceptualResourceBudget.ImageMemoryBytes - (shortage == "memory" ? 1 : 0);
        var pool = new PerceptualJobResourcePool();
        var resources = new PerceptualResourceAdmission(root => new(root, shortage == "disk-workspace" ? 1 : long.MaxValue, memory,
            shortage == "ram-workspace"), pool);
        var dispatcher = ActivatorUtilities.CreateInstance<QueueDispatcher>(api.Services,
            isolated.GetRequiredService<IServiceScopeFactory>(), resources);
        var reservations = new List<IDisposable>();
        try
        {
            var spec = new TranscodeSpec(path, Path.Combine(Path.GetDirectoryName(path)!, "absent.webp"), null, null, null, false,
                Kind: MediaKind.Image, ImageEncoder: "libwebp", ImageQuality: 80);
            var work = new QueueDispatcher.JobWork(spec, FfmpegCommandBuilder.Build(spec), null, null, false, false, null, null, null,
                false, null, false, 0, new(path, size, null, 0, 0, false, false, Kind: MediaKind.Image, VideoReencoded: false), policy,
                VideoQualityStrategy.Fixed, WorkPlacement.LocalOnly, null, false, 0, false, false,
                AdaptiveImageQuality: true, PerceptualSearch: retained, PerceptualRetryCount: retry ? 1 : 0);
            Assert.Null(await dispatcher.SelectPerceptualQualityAsync(jobId, 3, work, reservations, default));
            await using var scope = api.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var job = await db.Jobs.AsNoTracking().Include(j => j.MediaFile).SingleAsync(j => j.Id == jobId);
            Assert.Equal(JobStatus.Queued, job.Status);
            Assert.Equal(2, job.Attempt);
            Assert.Equal(3, job.ExecutionAttempt);
            Assert.True(dispatcher.IsLocallyHeldForCapacity(job.Id, job.ExecutionAttempt));
            Assert.Contains("Waiting", job.ErrorMessage);
            Assert.Null(job.FinishedAt);
            Assert.Equal(2, job.MediaFile!.FailureCount);
            Assert.Equal(retry ? 1 : 0, job.QualityRetryCount);
            Assert.Equal(retry ? 70 : (int?)null, QueueDispatcher.ReadPerceptualRetry(job)?.RetryValue);
            Assert.False(await db.Exclusions.AnyAsync(e => e.LibraryId == libraryId));
            Assert.False(await db.Replacements.AnyAsync(replacement => replacement.JobId == jobId));
            Assert.Equal(shortage.EndsWith("workspace", StringComparison.Ordinal) ? 1 : 0, reads);
            Assert.Equal("owned synthetic source", await File.ReadAllTextAsync(path));
        }
        finally
        {
            foreach (var reservation in reservations) reservation.Dispose();
            dispatcher.Dispose();
            await using var scope = api.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            db.Libraries.Remove(await db.Libraries.SingleAsync(l => l.Id == libraryId)); await db.SaveChangesAsync();
            File.Delete(path);
        }
    }
}
