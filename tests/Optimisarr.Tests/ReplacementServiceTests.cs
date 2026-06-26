using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Optimisarr.Api.Library;
using Optimisarr.Api.Replacement;
using Optimisarr.Api.Stats;
using Optimisarr.Core.Library;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class ReplacementServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;
    private readonly string _root;
    private readonly string _dataDir;
    private readonly string _workDir;
    private readonly string _trashDir;

    public ReplacementServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();

        _root = Path.Combine(Path.GetTempPath(), "optimisarr-tests", Guid.NewGuid().ToString("N"));
        _dataDir = Path.Combine(_root, "data");
        _workDir = Path.Combine(_root, "work");
        _trashDir = Path.Combine(_root, "trash");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_workDir);
    }

    [Fact]
    public async Task Replace_quarantines_the_original_and_puts_the_verified_output_in_place()
    {
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL-DATA", "NEW-SMALLER");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);

        var result = await ReplaceAsync(jobId);

        Assert.Equal(ReplacementResultKind.Success, result.Kind);

        var finalPath = Path.Combine(_dataDir, "Movie.mkv");
        Assert.True(File.Exists(finalPath));
        Assert.Equal("NEW-SMALLER", File.ReadAllText(finalPath));
        Assert.False(File.Exists(originalPath));   // original moved out of place
        Assert.False(File.Exists(outputPath));     // output moved out of the work dir

        var replacement = result.Replacement!;
        Assert.True(File.Exists(replacement.QuarantinePath));
        Assert.Equal("ORIGINAL-DATA", File.ReadAllText(replacement.QuarantinePath));
        Assert.Equal(ReplacementStatus.Replaced, replacement.Status);

        await using var db = new OptimisarrDbContext(_options);
        var job = await db.Jobs.SingleAsync();
        Assert.Equal(JobStatus.Completed, job.Status);
        var media = await db.MediaFiles.SingleAsync();
        Assert.Equal(finalPath, media.Path);
        Assert.Equal("NEW-SMALLER".Length, media.SizeBytes);
    }

    [Fact]
    public async Task Replace_handles_an_unchanged_container_by_replacing_in_place()
    {
        var (originalPath, outputPath) = WriteFiles("Show.mkv", "Show.mkv", "OLD", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);

        var result = await ReplaceAsync(jobId);

        Assert.Equal(ReplacementResultKind.Success, result.Kind);
        Assert.Equal(originalPath, result.Replacement!.FinalPath);
        Assert.Equal("NEW", File.ReadAllText(originalPath));
        Assert.Equal("OLD", File.ReadAllText(result.Replacement.QuarantinePath));
    }

    [Fact]
    public async Task Replace_fails_safely_when_a_different_file_already_occupies_the_destination()
    {
        // photo.tif optimises to photo.webp, but a photo.webp (from another source, e.g.
        // photo.bmp) already sits there. Replacing must not overwrite that different file.
        var (originalPath, outputPath) = WriteFiles("photo.tif", "photo.webp", "ORIGINAL-TIF", "NEW-FROM-TIF");
        var occupied = Path.Combine(_dataDir, "photo.webp");
        File.WriteAllText(occupied, "ALREADY-OPTIMISED-BMP");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);

        var result = await ReplaceAsync(jobId);

        Assert.Equal(ReplacementResultKind.Failed, result.Kind);
        Assert.Contains("collide", result.Message);
        // The original is untouched (never quarantined) and the occupant is intact.
        Assert.True(File.Exists(originalPath));
        Assert.Equal("ORIGINAL-TIF", File.ReadAllText(originalPath));
        Assert.Equal("ALREADY-OPTIMISED-BMP", File.ReadAllText(occupied));
        Assert.Equal("NEW-FROM-TIF", File.ReadAllText(outputPath));   // verified output retained
        Assert.Empty(new OptimisarrDbContext(_options).Replacements);
    }

    [Fact]
    public async Task Replace_refuses_a_job_that_has_not_passed_verification()
    {
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: false);

        var result = await ReplaceAsync(jobId);

        Assert.Equal(ReplacementResultKind.Invalid, result.Kind);
        Assert.True(File.Exists(originalPath));         // nothing touched
        Assert.Equal("ORIGINAL", File.ReadAllText(originalPath));
        Assert.Empty(new OptimisarrDbContext(_options).Replacements);
    }

    [Fact]
    public async Task Replace_refuses_cross_filesystem_moves_when_policy_disallows_them()
    {
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);

        var result = await ReplaceAsync(jobId, canMoveAtomically: (_, _) => false);

        Assert.Equal(ReplacementResultKind.Invalid, result.Kind);
        Assert.Contains("cross-filesystem", result.Message);
        Assert.True(File.Exists(originalPath));
        Assert.True(File.Exists(outputPath));
        Assert.Equal("ORIGINAL", File.ReadAllText(originalPath));
    }

    [Fact]
    public async Task Replace_allows_cross_filesystem_moves_when_policy_allows_them()
    {
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);
        await SetCrossFilesystemReplacementAsync(allowed: true);

        var result = await ReplaceAsync(jobId, canMoveAtomically: (_, _) => false);

        Assert.Equal(ReplacementResultKind.Success, result.Kind);
        Assert.False(File.Exists(originalPath));
        Assert.Equal("NEW", File.ReadAllText(result.Replacement!.FinalPath));
    }

    [Fact]
    public async Task Rollback_restores_the_original_and_removes_the_replacement_output()
    {
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL-DATA", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);
        var replaced = await ReplaceAsync(jobId);
        var finalPath = replaced.Replacement!.FinalPath;

        var result = await RollbackAsync(replaced.Replacement.Id);

        Assert.Equal(ReplacementResultKind.Success, result.Kind);
        Assert.True(File.Exists(originalPath));
        Assert.Equal("ORIGINAL-DATA", File.ReadAllText(originalPath));
        Assert.False(File.Exists(finalPath));   // the transcoded output is gone

        await using var db = new OptimisarrDbContext(_options);
        var replacement = await db.Replacements.SingleAsync();
        Assert.Equal(ReplacementStatus.RolledBack, replacement.Status);
        Assert.NotNull(replacement.RolledBackAt);
        var media = await db.MediaFiles.SingleAsync();
        Assert.Equal(originalPath, media.Path);
        Assert.Equal("ORIGINAL-DATA".Length, media.SizeBytes);
    }

    [Fact]
    public async Task Replace_accrues_the_lifetime_tally_and_rollback_reverses_it()
    {
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL-DATA", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);

        var replaced = await ReplaceAsync(jobId);
        var afterReplace = await ReadLifetimeAsync();

        Assert.Equal(1, afterReplace.FilesOptimised);
        Assert.Equal("ORIGINAL-DATA".Length, afterReplace.OriginalBytes);
        Assert.Equal("NEW".Length, afterReplace.OptimisedBytes);

        await RollbackAsync(replaced.Replacement!.Id);
        var afterRollback = await ReadLifetimeAsync();

        // The original is back, so the headline savings return to zero.
        Assert.Equal(LifetimeStats.Empty, afterRollback);
    }

    [Fact]
    public async Task Replace_restores_the_original_when_a_mid_move_failure_leaves_a_remnant_in_place()
    {
        // Same-container replacement (FinalPath == the original's own path). Simulate a
        // cross-filesystem copy that fails partway, leaving a partial output sitting at that path —
        // the exact case where a naive "restore only if the path is empty" guard would strand the
        // original in quarantine. The original must come back and the remnant must be cleared.
        var (originalPath, outputPath) = WriteFiles("Show.mkv", "Show.mkv", "ORIGINAL-DATA", "NEW-OUTPUT");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);

        var result = await ReplaceAsync(jobId, moveFile: (source, destination) =>
        {
            if (string.Equals(source, outputPath, StringComparison.Ordinal))
            {
                File.WriteAllText(destination, "PARTIAL-OUTPUT");   // a failed copy left a remnant
                throw new IOException("simulated mid-copy failure");
            }
            return FileMover.Move(source, destination);
        });

        Assert.Equal(ReplacementResultKind.Failed, result.Kind);
        Assert.True(File.Exists(originalPath));
        Assert.Equal("ORIGINAL-DATA", File.ReadAllText(originalPath));   // the protected original, not the remnant
        Assert.Empty(new OptimisarrDbContext(_options).Replacements);    // nothing recorded
        Assert.Equal(LifetimeStats.Empty, await ReadLifetimeAsync());    // a failed replace saves nothing
    }

    [Fact]
    public async Task Replace_fails_without_touching_the_original_when_the_verified_output_is_missing()
    {
        // The verified output vanished from /work (the exact condition that left job 3327 looping).
        // Replacement must bail before quarantining, leaving the original untouched and recording
        // nothing — the only safe outcome, since the output can no longer be put in place.
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);
        File.Delete(outputPath);

        var result = await ReplaceAsync(jobId);

        Assert.Equal(ReplacementResultKind.Failed, result.Kind);
        Assert.Contains("missing", result.Message);
        Assert.True(File.Exists(originalPath));
        Assert.Equal("ORIGINAL", File.ReadAllText(originalPath));
        Assert.Empty(new OptimisarrDbContext(_options).Replacements);
    }

    [Fact]
    public async Task Replace_serialises_concurrent_attempts_so_only_one_acts_on_a_job()
    {
        // Reproduces the job 3327 corruption: a job is replaceable the instant it reaches
        // ReadyToReplace, and two callers (post-verify auto-replace + the reconcile sweep) race for
        // it. The first holds the per-job claim while blocked mid-move; the second must back off
        // untouched instead of running the destructive move sequence in parallel.
        var (originalPath, outputPath) = WriteFiles("Show.mkv", "Show.mkv", "ORIGINAL-DATA", "NEW-OUTPUT");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);

        var coordinator = new ReplacementCoordinator();
        var firstMoveEntered = new TaskCompletionSource();
        var releaseFirstMove = new TaskCompletionSource();

        FileMoveResult BlockingMove(string source, string destination)
        {
            firstMoveEntered.TrySetResult();
            releaseFirstMove.Task.GetAwaiter().GetResult();   // keep holding the claim
            return FileMover.Move(source, destination);
        }

        var first = Task.Run(() => ReplaceAsync(jobId, moveFile: BlockingMove, coordinator: coordinator));
        await firstMoveEntered.Task;   // the first replace now owns the claim and is mid-move

        var second = await ReplaceAsync(jobId, coordinator: coordinator);

        releaseFirstMove.SetResult();
        var firstResult = await first;

        Assert.Equal(ReplacementResultKind.Invalid, second.Kind);
        Assert.Contains("already in progress", second.Message);

        Assert.Equal(ReplacementResultKind.Success, firstResult.Kind);
        var finalPath = Path.Combine(_dataDir, "Show.mkv");
        Assert.Equal("NEW-OUTPUT", File.ReadAllText(finalPath));
        Assert.Equal("ORIGINAL-DATA", File.ReadAllText(firstResult.Replacement!.QuarantinePath));
        Assert.Single(new OptimisarrDbContext(_options).Replacements);
    }

    [Fact]
    public async Task Replace_releases_its_per_job_claim_after_finishing()
    {
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);
        var coordinator = new ReplacementCoordinator();

        var result = await ReplaceAsync(jobId, coordinator: coordinator);

        Assert.Equal(ReplacementResultKind.Success, result.Kind);
        Assert.True(coordinator.TryBegin(jobId));   // the claim was freed for a later cycle
    }

    [Fact]
    public async Task Rollback_refuses_an_already_rolled_back_replacement()
    {
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);
        var replaced = await ReplaceAsync(jobId);
        await RollbackAsync(replaced.Replacement!.Id);

        var second = await RollbackAsync(replaced.Replacement.Id);

        Assert.Equal(ReplacementResultKind.Invalid, second.Kind);
    }

    private (string OriginalPath, string OutputPath) WriteFiles(
        string originalName, string outputName, string originalContent, string outputContent)
    {
        var originalPath = Path.Combine(_dataDir, originalName);
        var outputPath = Path.Combine(_workDir, outputName);
        File.WriteAllText(originalPath, originalContent);
        File.WriteAllText(outputPath, outputContent);
        return (originalPath, outputPath);
    }

    private async Task<int> SeedReadyJobAsync(string originalPath, string outputPath, bool verificationPassed)
    {
        await using var db = new OptimisarrDbContext(_options);
        var media = new MediaFile
        {
            Path = originalPath,
            RelativePath = Path.GetFileName(originalPath),
            SizeBytes = new FileInfo(originalPath).Length,
            Status = MediaFileStatus.Probed
        };
        db.MediaFiles.Add(media);
        await db.SaveChangesAsync();

        var job = new Job
        {
            MediaFileId = media.Id,
            Status = JobStatus.ReadyToReplace,
            WorkOutputPath = outputPath,
            VerificationPassed = verificationPassed,
            VerifiedAt = DateTimeOffset.UtcNow,
            Progress = 1.0
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private async Task<ReplacementActionResult> ReplaceAsync(
        int jobId,
        Func<string, string, bool>? canMoveAtomically = null,
        Func<string, string, FileMoveResult>? moveFile = null,
        ReplacementCoordinator? coordinator = null)
    {
        await using var db = new OptimisarrDbContext(_options);
        var service = NewService(db, canMoveAtomically, moveFile, coordinator);
        return await service.ReplaceAsync(jobId, CancellationToken.None);
    }

    private async Task<LifetimeStats> ReadLifetimeAsync()
    {
        await using var db = new OptimisarrDbContext(_options);
        return await new LifetimeStatsStore(db).GetAsync(CancellationToken.None);
    }

    private async Task<ReplacementActionResult> RollbackAsync(int replacementId)
    {
        await using var db = new OptimisarrDbContext(_options);
        var service = NewService(db);
        return await service.RollbackAsync(replacementId, CancellationToken.None);
    }

    private ReplacementService NewService(
        OptimisarrDbContext db,
        Func<string, string, bool>? canMoveAtomically = null,
        Func<string, string, FileMoveResult>? moveFile = null,
        ReplacementCoordinator? coordinator = null)
    {
        var inventory = new LibraryInventoryService(db, new LibraryScanner(), new MediaProbeService(), new ImageMarkerService());
        return new ReplacementService(
            db,
            inventory,
            new SettingsStore(db),
            _trashDir,
            NullLogger<ReplacementService>.Instance,
            canMoveAtomically,
            moveFile: moveFile,
            coordinator: coordinator);
    }

    private async Task SetCrossFilesystemReplacementAsync(bool allowed)
    {
        await using var db = new OptimisarrDbContext(_options);
        var settings = new SettingsStore(db);
        var current = await settings.GetQueueSettingsAsync(CancellationToken.None);
        await settings.SetQueueSettingsAsync(current with { ReplacementAllowCrossFilesystem = allowed }, CancellationToken.None);
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
