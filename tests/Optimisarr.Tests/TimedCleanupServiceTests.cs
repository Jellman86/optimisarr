using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Optimisarr.Api.Library;
using Optimisarr.Api.Replacement;
using Optimisarr.Data;

namespace Optimisarr.Tests;

/// <summary>Filesystem and persistence safety coverage for the shared timed-retention sweep.</summary>
public sealed class TimedCleanupServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;
    private readonly string _trashDir;
    private readonly string _workDir;

    public TimedCleanupServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();

        _trashDir = Path.Combine(Path.GetTempPath(), "optimisarr-tests", Guid.NewGuid().ToString("N"));
        _workDir = Path.Combine(Path.GetTempPath(), "optimisarr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_trashDir);
        Directory.CreateDirectory(_workDir);
    }

    [Fact]
    public async Task Purge_does_nothing_when_retention_is_indefinite()
    {
        await SetRetentionDaysAsync(0);
        var (id, quarantinePath) = await SeedQuarantinedAsync(replacedAt: DateTimeOffset.UtcNow.AddYears(-1));
        var (jobId, outputPath) = await SeedFailedWorkOutputAsync(DateTimeOffset.UtcNow.AddYears(-1));

        var purged = await PurgeAsync();

        Assert.Equal(0, purged);
        Assert.True(File.Exists(quarantinePath));
        Assert.True(File.Exists(outputPath));
        Assert.Equal(ReplacementStatus.Replaced, await StatusOfAsync(id));
        await using var db = new OptimisarrDbContext(_options);
        Assert.Equal(outputPath, (await db.Jobs.SingleAsync(job => job.Id == jobId)).WorkOutputPath);
    }

    [Fact]
    public async Task Preview_reports_only_currently_eligible_disk_usage_without_mutating_anything()
    {
        await SetRetentionDaysAsync(7);
        var (replacementId, quarantinePath) = await SeedQuarantinedAsync(DateTimeOffset.UtcNow.AddDays(-8));
        var (jobId, outputPath) = await SeedFailedWorkOutputAsync(DateTimeOffset.UtcNow.AddDays(-8));
        await SeedFailedWorkOutputAsync(DateTimeOffset.UtcNow.AddDays(-6));

        var preview = await PreviewAsync();

        Assert.Equal(7, preview.RetentionDays);
        Assert.False(preview.DryRunMode);
        Assert.Equal(1, preview.QuarantinedOriginalCount);
        Assert.Equal(new FileInfo(quarantinePath).Length, preview.QuarantinedOriginalBytes);
        Assert.Equal(1, preview.FailedOutputCount);
        Assert.Equal(new FileInfo(outputPath).Length, preview.FailedOutputBytes);
        Assert.Equal(2, preview.TotalCount);
        Assert.Equal(
            new FileInfo(quarantinePath).Length + new FileInfo(outputPath).Length,
            preview.TotalBytes);
        Assert.True(File.Exists(quarantinePath));
        Assert.True(File.Exists(outputPath));
        Assert.Equal(ReplacementStatus.Replaced, await StatusOfAsync(replacementId));
        await using var db = new OptimisarrDbContext(_options);
        Assert.Equal(outputPath, (await db.Jobs.SingleAsync(job => job.Id == jobId)).WorkOutputPath);
    }

    [Fact]
    public async Task Preview_excludes_quarantined_originals_in_dry_run_but_includes_failed_scratch()
    {
        await SetRetentionDaysAsync(7);
        await SetDryRunModeAsync(true);
        var (_, quarantinePath) = await SeedQuarantinedAsync(DateTimeOffset.UtcNow.AddDays(-8));
        var (_, outputPath) = await SeedFailedWorkOutputAsync(DateTimeOffset.UtcNow.AddDays(-8));

        var preview = await PreviewAsync();

        Assert.True(preview.DryRunMode);
        Assert.Equal(0, preview.QuarantinedOriginalCount);
        Assert.Equal(0, preview.QuarantinedOriginalBytes);
        Assert.Equal(1, preview.FailedOutputCount);
        Assert.Equal(new FileInfo(outputPath).Length, preview.TotalBytes);
        Assert.True(File.Exists(quarantinePath));
        Assert.True(File.Exists(outputPath));
    }

    [Fact]
    public async Task Preview_reports_nothing_when_retention_is_indefinite()
    {
        await SetRetentionDaysAsync(0);
        await SeedQuarantinedAsync(DateTimeOffset.UtcNow.AddYears(-1));
        await SeedFailedWorkOutputAsync(DateTimeOffset.UtcNow.AddYears(-1));

        var preview = await PreviewAsync();

        Assert.Equal(0, preview.RetentionDays);
        Assert.Equal(0, preview.TotalCount);
        Assert.Equal(0, preview.TotalBytes);
    }

    [Fact]
    public async Task Run_returns_the_confirmed_preview_and_actual_reclaimed_bytes()
    {
        await SetRetentionDaysAsync(7);
        var (_, quarantinePath) = await SeedQuarantinedAsync(DateTimeOffset.UtcNow.AddDays(-8));
        var (_, outputPath) = await SeedFailedWorkOutputAsync(DateTimeOffset.UtcNow.AddDays(-8));
        var expectedBytes = new FileInfo(quarantinePath).Length + new FileInfo(outputPath).Length;

        var result = await RunAsync();

        Assert.Equal(2, result.Preview.TotalCount);
        Assert.Equal(expectedBytes, result.Preview.TotalBytes);
        Assert.Equal(2, result.CleanedCount);
        Assert.Equal(expectedBytes, result.ReclaimedBytes);
        Assert.False(File.Exists(quarantinePath));
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task Confirmed_run_refuses_a_plan_that_changed_after_the_preview()
    {
        await SetRetentionDaysAsync(7);
        var (jobId, firstPath) = await SeedFailedWorkOutputAsync(DateTimeOffset.UtcNow.AddDays(-8));
        var preview = await PreviewAsync();

        // Swap the eligible identity for a same-sized file so aggregate counts and bytes cannot
        // accidentally authorize a different plan than the one the operator confirmed.
        var secondDirectory = Path.Combine(_workDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(secondDirectory);
        var secondPath = Path.Combine(secondDirectory, "candidate.mp4");
        File.WriteAllText(secondPath, "candidate");
        File.SetLastWriteTimeUtc(secondPath, DateTime.UtcNow.AddDays(-8));
        await using (var db = new OptimisarrDbContext(_options))
        {
            var job = await db.Jobs.SingleAsync(item => item.Id == jobId);
            job.WorkOutputPath = secondPath;
            await db.SaveChangesAsync();
        }

        var attempt = await RunConfirmedAsync(preview);

        Assert.Null(attempt.Result);
        Assert.Equal(preview.FailedOutputCount, attempt.CurrentPreview.FailedOutputCount);
        Assert.Equal(preview.FailedOutputBytes, attempt.CurrentPreview.FailedOutputBytes);
        Assert.NotEqual(preview.PlanToken, attempt.CurrentPreview.PlanToken);
        Assert.True(File.Exists(firstPath));
        Assert.True(File.Exists(secondPath));
    }

    [Fact]
    public async Task Purge_deletes_originals_past_the_retention_window_and_marks_them_purged()
    {
        await SetRetentionDaysAsync(30);
        var (id, quarantinePath) = await SeedQuarantinedAsync(replacedAt: DateTimeOffset.UtcNow.AddDays(-31));
        var stampDir = Path.GetDirectoryName(quarantinePath)!;

        var purged = await PurgeAsync();

        Assert.Equal(1, purged);
        Assert.False(File.Exists(quarantinePath));   // original deleted
        Assert.False(Directory.Exists(stampDir));     // empty quarantine folder removed too

        await using var db = new OptimisarrDbContext(_options);
        var replacement = await db.Replacements.SingleAsync();
        Assert.Equal(ReplacementStatus.Purged, replacement.Status);
        Assert.NotNull(replacement.PurgedAt);
    }

    [Fact]
    public async Task Purge_does_nothing_while_dry_run_mode_is_enabled()
    {
        await SetRetentionDaysAsync(30);
        await SetDryRunModeAsync(true);
        var (id, quarantinePath) = await SeedQuarantinedAsync(replacedAt: DateTimeOffset.UtcNow.AddDays(-31));

        var purged = await PurgeAsync();

        Assert.Equal(0, purged);
        Assert.True(File.Exists(quarantinePath));
        Assert.Equal(ReplacementStatus.Replaced, await StatusOfAsync(id));
    }

    [Fact]
    public async Task Purge_keeps_originals_still_within_the_retention_window()
    {
        await SetRetentionDaysAsync(30);
        var (id, quarantinePath) = await SeedQuarantinedAsync(replacedAt: DateTimeOffset.UtcNow.AddDays(-5));

        var purged = await PurgeAsync();

        Assert.Equal(0, purged);
        Assert.True(File.Exists(quarantinePath));
        Assert.Equal(ReplacementStatus.Replaced, await StatusOfAsync(id));
    }

    [Fact]
    public async Task Purge_deletes_expired_failed_work_output_but_keeps_diagnostics()
    {
        await SetRetentionDaysAsync(7);
        var (jobId, outputPath) = await SeedFailedWorkOutputAsync(DateTimeOffset.UtcNow.AddDays(-8));

        var purged = await PurgeAsync();

        Assert.Equal(1, purged);
        Assert.False(File.Exists(outputPath));
        await using var db = new OptimisarrDbContext(_options);
        var job = await db.Jobs.SingleAsync(item => item.Id == jobId);
        Assert.Null(job.WorkOutputPath);
        Assert.Equal(1234, job.OutputSizeBytes);
        Assert.Equal("Verification failed: Perceptual quality (VMAF)", job.ErrorMessage);
        Assert.Equal("{\"passed\":false}", job.VerificationReportJson);
        Assert.Equal("ffmpeg diagnostic output", job.ProcessLog);
    }

    [Fact]
    public async Task Purge_keeps_failed_work_output_within_retention_window()
    {
        await SetRetentionDaysAsync(7);
        var (jobId, outputPath) = await SeedFailedWorkOutputAsync(DateTimeOffset.UtcNow.AddDays(-6));

        var purged = await PurgeAsync();

        Assert.Equal(0, purged);
        Assert.True(File.Exists(outputPath));
        await using var db = new OptimisarrDbContext(_options);
        Assert.Equal(outputPath, (await db.Jobs.SingleAsync(item => item.Id == jobId)).WorkOutputPath);
    }

    [Theory]
    [InlineData(JobStatus.Queued)]
    [InlineData(JobStatus.Transcoding)]
    [InlineData(JobStatus.Verifying)]
    [InlineData(JobStatus.ReadyToReplace)]
    [InlineData(JobStatus.Completed)]
    public async Task Purge_never_deletes_non_failed_work_output(JobStatus status)
    {
        await SetRetentionDaysAsync(1);
        var (jobId, outputPath) = await SeedWorkOutputAsync(status, DateTimeOffset.UtcNow.AddDays(-30));

        var purged = await PurgeAsync();

        Assert.Equal(0, purged);
        Assert.True(File.Exists(outputPath));
        await using var db = new OptimisarrDbContext(_options);
        Assert.Equal(outputPath, (await db.Jobs.SingleAsync(item => item.Id == jobId)).WorkOutputPath);
    }

    [Fact]
    public async Task Purge_never_deletes_a_failed_output_outside_the_owned_work_root()
    {
        await SetRetentionDaysAsync(1);
        var outside = Path.Combine(_trashDir, "failed-output.mp4");
        File.WriteAllText(outside, "candidate");
        var jobId = await SeedFailedJobAsync(outside, DateTimeOffset.UtcNow.AddDays(-30));

        var purged = await PurgeAsync();

        Assert.Equal(0, purged);
        Assert.True(File.Exists(outside));
        await using var db = new OptimisarrDbContext(_options);
        Assert.Equal(outside, (await db.Jobs.SingleAsync(item => item.Id == jobId)).WorkOutputPath);
    }

    [Fact]
    public async Task Purge_keeps_a_path_referenced_by_an_active_job()
    {
        await SetRetentionDaysAsync(1);
        var (_, outputPath) = await SeedFailedWorkOutputAsync(DateTimeOffset.UtcNow.AddDays(-30));
        await SeedFailedJobAsync(outputPath, DateTimeOffset.UtcNow, JobStatus.Transcoding);

        var purged = await PurgeAsync();

        Assert.Equal(0, purged);
        Assert.True(File.Exists(outputPath));
    }

    [Fact]
    public async Task Purge_keeps_a_recently_recreated_failed_path()
    {
        await SetRetentionDaysAsync(1);
        var (jobId, outputPath) = await SeedFailedWorkOutputAsync(DateTimeOffset.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(outputPath, DateTime.UtcNow);

        var purged = await PurgeAsync();

        Assert.Equal(0, purged);
        Assert.True(File.Exists(outputPath));
        await using var db = new OptimisarrDbContext(_options);
        Assert.Equal(outputPath, (await db.Jobs.SingleAsync(item => item.Id == jobId)).WorkOutputPath);
    }

    [Fact]
    public async Task Purge_clears_an_expired_missing_output_reference()
    {
        await SetRetentionDaysAsync(1);
        var (jobId, outputPath) = await SeedFailedWorkOutputAsync(DateTimeOffset.UtcNow.AddDays(-30));
        File.Delete(outputPath);

        var purged = await PurgeAsync();

        Assert.Equal(1, purged);
        await using var db = new OptimisarrDbContext(_options);
        Assert.Null((await db.Jobs.SingleAsync(item => item.Id == jobId)).WorkOutputPath);
    }

    [Fact]
    public async Task Purge_leaves_failed_calibration_evidence_to_disposable_cleanup()
    {
        await SetRetentionDaysAsync(1);
        var directory = Path.Combine(_workDir, "calibration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var outputPath = Path.Combine(directory, "candidate.mp4");
        File.WriteAllText(outputPath, "candidate");
        var jobId = await SeedFailedJobAsync(
            outputPath,
            DateTimeOffset.UtcNow.AddDays(-30),
            JobStatus.Failed,
            JobType.Calibration);

        var purged = await PurgeAsync();

        Assert.Equal(0, purged);
        Assert.True(File.Exists(outputPath));
        await using var db = new OptimisarrDbContext(_options);
        Assert.Equal(outputPath, (await db.Jobs.SingleAsync(item => item.Id == jobId)).WorkOutputPath);
    }

    [Fact]
    public async Task Dry_run_still_cleans_expired_failed_scratch_without_purging_originals()
    {
        await SetRetentionDaysAsync(7);
        await SetDryRunModeAsync(true);
        var (replacementId, quarantinePath) = await SeedQuarantinedAsync(DateTimeOffset.UtcNow.AddDays(-30));
        var (_, outputPath) = await SeedFailedWorkOutputAsync(DateTimeOffset.UtcNow.AddDays(-8));

        var purged = await PurgeAsync();

        Assert.Equal(1, purged);
        Assert.False(File.Exists(outputPath));
        Assert.True(File.Exists(quarantinePath));
        Assert.Equal(ReplacementStatus.Replaced, await StatusOfAsync(replacementId));
    }

    [Fact]
    public async Task Purge_ignores_already_rolled_back_replacements()
    {
        await SetRetentionDaysAsync(30);
        var (id, _) = await SeedQuarantinedAsync(
            replacedAt: DateTimeOffset.UtcNow.AddYears(-1),
            status: ReplacementStatus.RolledBack);

        var purged = await PurgeAsync();

        Assert.Equal(0, purged);
        Assert.Equal(ReplacementStatus.RolledBack, await StatusOfAsync(id));
    }

    [Fact]
    public async Task Purge_still_marks_an_entry_purged_when_the_file_is_already_gone()
    {
        await SetRetentionDaysAsync(30);
        var (id, quarantinePath) = await SeedQuarantinedAsync(replacedAt: DateTimeOffset.UtcNow.AddDays(-40));
        File.Delete(quarantinePath);   // someone removed it manually

        var purged = await PurgeAsync();

        Assert.Equal(1, purged);
        Assert.Equal(ReplacementStatus.Purged, await StatusOfAsync(id));
    }

    [Fact]
    public async Task Purge_one_deletes_the_original_now_regardless_of_the_retention_window()
    {
        await SetRetentionDaysAsync(0); // indefinite retention — an on-demand approve still purges
        var (id, quarantinePath) = await SeedQuarantinedAsync(replacedAt: DateTimeOffset.UtcNow);
        var stampDir = Path.GetDirectoryName(quarantinePath)!;

        var result = await PurgeOneAsync(id);

        Assert.Equal(ReplacementResultKind.Success, result.Kind);
        Assert.False(File.Exists(quarantinePath));
        Assert.False(Directory.Exists(stampDir));
        Assert.Equal(ReplacementStatus.Purged, await StatusOfAsync(id));

        await using var db = new OptimisarrDbContext(_options);
        Assert.NotNull((await db.Replacements.SingleAsync(r => r.Id == id)).PurgedAt);
    }

    [Fact]
    public async Task Purge_one_refuses_while_dry_run_mode_is_enabled()
    {
        await SetDryRunModeAsync(true);
        var (id, quarantinePath) = await SeedQuarantinedAsync(replacedAt: DateTimeOffset.UtcNow);

        var result = await PurgeOneAsync(id);

        Assert.Equal(ReplacementResultKind.Invalid, result.Kind);
        Assert.Contains("Dry-run", result.Message);
        Assert.True(File.Exists(quarantinePath));
        Assert.Equal(ReplacementStatus.Replaced, await StatusOfAsync(id));
    }

    [Fact]
    public async Task Purge_one_refuses_a_replacement_that_is_not_in_quarantine()
    {
        var (id, quarantinePath) = await SeedQuarantinedAsync(
            replacedAt: DateTimeOffset.UtcNow, status: ReplacementStatus.RolledBack);

        var result = await PurgeOneAsync(id);

        Assert.Equal(ReplacementResultKind.Invalid, result.Kind);
        Assert.True(File.Exists(quarantinePath));                       // nothing deleted
        Assert.Equal(ReplacementStatus.RolledBack, await StatusOfAsync(id));
    }

    [Fact]
    public async Task Purge_one_reports_not_found_for_an_unknown_id()
    {
        var result = await PurgeOneAsync(9999);

        Assert.Equal(ReplacementResultKind.NotFound, result.Kind);
    }

    private async Task<ReplacementActionResult> PurgeOneAsync(int replacementId)
    {
        await using var db = new OptimisarrDbContext(_options);
        var service = new TimedCleanupService(
            db, new SettingsStore(db), _workDir, NullLogger<TimedCleanupService>.Instance);
        return await service.PurgeOneAsync(replacementId, CancellationToken.None);
    }

    private async Task<int> PurgeAsync()
    {
        await using var db = new OptimisarrDbContext(_options);
        var service = new TimedCleanupService(
            db, new SettingsStore(db), _workDir, NullLogger<TimedCleanupService>.Instance);
        return await service.PurgeExpiredAsync(CancellationToken.None);
    }

    private async Task<TimedCleanupPreview> PreviewAsync()
    {
        await using var db = new OptimisarrDbContext(_options);
        var service = new TimedCleanupService(
            db, new SettingsStore(db), _workDir, NullLogger<TimedCleanupService>.Instance);
        return await service.PreviewExpiredAsync(CancellationToken.None);
    }

    private async Task<TimedCleanupRunResult> RunAsync()
    {
        await using var db = new OptimisarrDbContext(_options);
        var service = new TimedCleanupService(
            db, new SettingsStore(db), _workDir, NullLogger<TimedCleanupService>.Instance);
        return await service.RunExpiredAsync(CancellationToken.None);
    }

    private async Task<TimedCleanupAttempt> RunConfirmedAsync(TimedCleanupPreview confirmedPreview)
    {
        await using var db = new OptimisarrDbContext(_options);
        var service = new TimedCleanupService(
            db, new SettingsStore(db), _workDir, NullLogger<TimedCleanupService>.Instance);
        return await service.RunConfirmedExpiredAsync(confirmedPreview, CancellationToken.None);
    }

    private Task<(int Id, string OutputPath)> SeedFailedWorkOutputAsync(DateTimeOffset finishedAt) =>
        SeedWorkOutputAsync(JobStatus.Failed, finishedAt);

    private async Task<(int Id, string OutputPath)> SeedWorkOutputAsync(
        JobStatus status,
        DateTimeOffset finishedAt)
    {
        var directory = Path.Combine(_workDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var outputPath = Path.Combine(directory, "candidate.mp4");
        File.WriteAllText(outputPath, "candidate");
        File.SetLastWriteTimeUtc(outputPath, finishedAt.UtcDateTime);
        var id = await SeedFailedJobAsync(outputPath, finishedAt, status);
        return (id, outputPath);
    }

    private async Task<int> SeedFailedJobAsync(
        string outputPath,
        DateTimeOffset finishedAt,
        JobStatus status = JobStatus.Failed,
        JobType type = JobType.Normal)
    {
        await using var db = new OptimisarrDbContext(_options);
        var media = new MediaFile
        {
            Path = Path.Combine(_trashDir, $"source-{Guid.NewGuid():N}.mkv"),
            RelativePath = "Movie.mkv",
            SizeBytes = 2000,
            Status = MediaFileStatus.Probed
        };
        db.MediaFiles.Add(media);
        await db.SaveChangesAsync();

        var job = new Job
        {
            MediaFileId = media.Id,
            Type = type,
            Status = status,
            WorkOutputPath = outputPath,
            OutputSizeBytes = 1234,
            ErrorMessage = "Verification failed: Perceptual quality (VMAF)",
            VerificationReportJson = "{\"passed\":false}",
            ProcessLog = "ffmpeg diagnostic output",
            FinishedAt = finishedAt,
            UpdatedAt = finishedAt
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private async Task<(int Id, string QuarantinePath)> SeedQuarantinedAsync(
        DateTimeOffset replacedAt,
        ReplacementStatus status = ReplacementStatus.Replaced)
    {
        var stampDir = Path.Combine(_trashDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stampDir);
        var quarantinePath = Path.Combine(stampDir, "Movie.avi");
        File.WriteAllText(quarantinePath, "ORIGINAL-DATA");

        await using var db = new OptimisarrDbContext(_options);
        var media = new MediaFile
        {
            Path = Path.Combine(_trashDir, $"placeholder-{Guid.NewGuid():N}.mkv"),
            RelativePath = "Movie.mkv",
            SizeBytes = 1,
            Status = MediaFileStatus.Probed
        };
        db.MediaFiles.Add(media);
        await db.SaveChangesAsync();

        var job = new Job { MediaFileId = media.Id, Status = JobStatus.Completed };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        var replacement = new Optimisarr.Data.Replacement
        {
            JobId = job.Id,
            MediaFileId = media.Id,
            OriginalPath = "/data/Movie.avi",
            QuarantinePath = quarantinePath,
            FinalPath = "/data/Movie.mkv",
            OriginalSizeBytes = 13,
            NewSizeBytes = 7,
            Status = status,
            ReplacedAt = replacedAt
        };
        db.Replacements.Add(replacement);
        await db.SaveChangesAsync();
        return (replacement.Id, quarantinePath);
    }

    private async Task SetRetentionDaysAsync(int days)
    {
        await using var db = new OptimisarrDbContext(_options);
        var settings = new SettingsStore(db);
        var current = await settings.GetQueueSettingsAsync(CancellationToken.None);
        await settings.SetQueueSettingsAsync(
            current with { ReplacementQuarantineRetentionDays = days }, CancellationToken.None);
    }

    private async Task SetDryRunModeAsync(bool enabled)
    {
        await using var db = new OptimisarrDbContext(_options);
        var settings = new SettingsStore(db);
        var current = await settings.GetQueueSettingsAsync(CancellationToken.None);
        await settings.SetQueueSettingsAsync(
            current with { DryRunMode = enabled }, CancellationToken.None);
    }

    private async Task<ReplacementStatus> StatusOfAsync(int replacementId)
    {
        await using var db = new OptimisarrDbContext(_options);
        var replacement = await db.Replacements.SingleAsync(r => r.Id == replacementId);
        return replacement.Status;
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_trashDir))
        {
            Directory.Delete(_trashDir, recursive: true);
        }
        if (Directory.Exists(_workDir))
        {
            Directory.Delete(_workDir, recursive: true);
        }
    }
}
