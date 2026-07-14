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
        // The occupant will never disappear on its own, so retrying every reconcile cycle would
        // loop forever; the failure is permanent so the dispatcher can fail the job once.
        Assert.True(result.Permanent);
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
    public async Task Replace_refuses_while_dry_run_mode_is_enabled()
    {
        await SetDryRunModeAsync(true);
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);

        var result = await ReplaceAsync(jobId);

        Assert.Equal(ReplacementResultKind.Invalid, result.Kind);
        Assert.Contains("Dry-run", result.Message);
        Assert.True(File.Exists(originalPath));
        Assert.Equal("ORIGINAL", File.ReadAllText(originalPath));
        Assert.True(File.Exists(outputPath));
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
        // A mid-move I/O failure can clear (disk frees up, permissions fixed), so it stays retryable.
        Assert.False(result.Permanent);
        Assert.True(File.Exists(originalPath));
        Assert.Equal("ORIGINAL-DATA", File.ReadAllText(originalPath));   // the protected original, not the remnant
        Assert.Empty(new OptimisarrDbContext(_options).Replacements);    // nothing recorded
        Assert.Equal(LifetimeStats.Empty, await ReadLifetimeAsync());    // a failed replace saves nothing
    }

    [Fact]
    public async Task Replace_records_the_rollback_path_before_the_first_file_move()
    {
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);
        var pendingWasRecorded = false;

        var result = await ReplaceAsync(jobId, moveFile: (_, _) =>
        {
            using var readDb = new OptimisarrDbContext(_options);
            pendingWasRecorded = readDb.Replacements.Any(replacement =>
                replacement.JobId == jobId && replacement.Status == ReplacementStatus.Pending);
            throw new IOException("stop after checking the durable rollback record");
        });

        Assert.True(pendingWasRecorded);
        Assert.Equal(ReplacementResultKind.Failed, result.Kind);
        Assert.Equal("ORIGINAL", File.ReadAllText(originalPath));
        Assert.Empty(new OptimisarrDbContext(_options).Replacements);
    }

    [Fact]
    public async Task Recovery_restores_a_pending_original_quarantined_before_a_crash()
    {
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);
        var quarantinePath = Path.Combine(_trashDir, "pending-job", "Movie.avi");
        var finalPath = Path.Combine(_dataDir, "Movie.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(quarantinePath)!);
        File.Move(originalPath, quarantinePath);
        await SeedPendingReplacementAsync(jobId, originalPath, finalPath, quarantinePath);

        await using var db = new OptimisarrDbContext(_options);
        var service = NewService(db);
        await service.RecoverPendingAsync(CancellationToken.None);
        Assert.Equal(0, await service.RecoverPendingAsync(CancellationToken.None));

        Assert.Equal("ORIGINAL", File.ReadAllText(originalPath));
        Assert.Equal("NEW", File.ReadAllText(outputPath));
        Assert.False(File.Exists(quarantinePath));
        Assert.Empty(await db.Replacements.ToListAsync());
    }

    [Fact]
    public async Task Recovery_finalizes_a_pending_replacement_whose_file_moves_completed_before_a_crash()
    {
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);
        var quarantinePath = Path.Combine(_trashDir, "pending-job", "Movie.avi");
        var finalPath = Path.Combine(_dataDir, "Movie.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(quarantinePath)!);
        File.Move(originalPath, quarantinePath);
        File.Move(outputPath, finalPath);
        await SeedPendingReplacementAsync(jobId, originalPath, finalPath, quarantinePath);

        await using var db = new OptimisarrDbContext(_options);
        var service = NewService(db);
        await service.RecoverPendingAsync(CancellationToken.None);
        Assert.Equal(0, await service.RecoverPendingAsync(CancellationToken.None));

        var replacement = await db.Replacements.SingleAsync();
        Assert.Equal(ReplacementStatus.Replaced, replacement.Status);
        Assert.Equal("ORIGINAL", File.ReadAllText(quarantinePath));
        Assert.Equal("NEW", File.ReadAllText(finalPath));
        Assert.Equal(JobStatus.Completed, (await db.Jobs.SingleAsync()).Status);
        Assert.Equal(finalPath, (await db.MediaFiles.SingleAsync()).Path);
        Assert.Equal(1, (await ReadLifetimeAsync()).FilesOptimised);
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
        // The output won't reappear, so this is permanent: the dispatcher fails the job once
        // instead of reconciling it forever (the loop that stranded job 3327).
        Assert.True(result.Permanent);
        Assert.True(File.Exists(originalPath));
        Assert.Equal("ORIGINAL", File.ReadAllText(originalPath));
        Assert.Empty(new OptimisarrDbContext(_options).Replacements);
    }

    [Fact]
    public async Task Replace_fails_permanently_when_the_original_has_vanished()
    {
        // The original was deleted or moved out from under the job (e.g. a Sonarr/Radarr upgrade
        // renamed it) between verification and replacement. There is nothing to quarantine, so
        // replacement must bail without creating anything, and the failure is permanent so the
        // reconcile sweep fails the job once instead of retrying it forever.
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);
        File.Delete(originalPath);

        var result = await ReplaceAsync(jobId);

        Assert.Equal(ReplacementResultKind.Failed, result.Kind);
        Assert.Contains("no longer exists", result.Message);
        Assert.True(result.Permanent);
        Assert.False(File.Exists(originalPath));                       // still gone; nothing recreated it
        Assert.Equal("NEW", File.ReadAllText(outputPath));             // the verified output is retained
        Assert.Empty(new OptimisarrDbContext(_options).Replacements);  // nothing recorded
        Assert.Equal(LifetimeStats.Empty, await ReadLifetimeAsync());  // a failed replace saves nothing
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

    [Fact]
    public async Task Rollback_fails_and_preserves_the_optimised_file_when_the_quarantined_original_is_missing()
    {
        // The quarantined original was purged or lost between the replacement and the rollback
        // attempt. Rollback must not delete the in-place optimised file when it has nothing to
        // restore — that would lose both copies. It must fail cleanly and leave everything as-is so
        // the replacement stays valid and replayable.
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);
        var replaced = await ReplaceAsync(jobId);
        var finalPath = replaced.Replacement!.FinalPath;
        File.Delete(replaced.Replacement.QuarantinePath);

        var result = await RollbackAsync(replaced.Replacement.Id);

        Assert.Equal(ReplacementResultKind.Failed, result.Kind);
        Assert.Contains("missing", result.Message);
        // The optimised file is still in place and the replacement is untouched — nothing was lost.
        Assert.True(File.Exists(finalPath));
        Assert.Equal("NEW", File.ReadAllText(finalPath));
        await using var db = new OptimisarrDbContext(_options);
        var replacement = await db.Replacements.SingleAsync();
        Assert.Equal(ReplacementStatus.Replaced, replacement.Status);
        Assert.Null(replacement.RolledBackAt);
    }

    [Fact]
    public async Task Rollback_restores_the_optimised_file_when_restoring_the_original_fails()
    {
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);
        var replaced = await ReplaceAsync(jobId);
        var replacement = replaced.Replacement!;

        var result = await RollbackAsync(replacement.Id, (source, destination) =>
        {
            if (string.Equals(source, replacement.QuarantinePath, StringComparison.Ordinal))
            {
                throw new IOException("simulated restore failure");
            }

            return FileMover.Move(source, destination);
        });

        Assert.Equal(ReplacementResultKind.Failed, result.Kind);
        Assert.Equal("NEW", File.ReadAllText(replacement.FinalPath));
        Assert.Equal("ORIGINAL", File.ReadAllText(replacement.QuarantinePath));
        await using var db = new OptimisarrDbContext(_options);
        Assert.Equal(ReplacementStatus.Replaced, (await db.Replacements.SingleAsync()).Status);
    }

    [Fact]
    public async Task Rollback_records_its_intent_before_staging_the_optimised_file()
    {
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);
        var replaced = await ReplaceAsync(jobId);
        var replacement = replaced.Replacement!;
        var rollbackWasRecorded = false;

        var result = await RollbackAsync(replacement.Id, (_, _) =>
        {
            using var readDb = new OptimisarrDbContext(_options);
            rollbackWasRecorded = readDb.Replacements.Any(row =>
                row.Id == replacement.Id && row.Status == ReplacementStatus.RollbackPending);
            throw new IOException("stop after checking rollback intent");
        });

        Assert.True(rollbackWasRecorded);
        Assert.Equal(ReplacementResultKind.Failed, result.Kind);
        Assert.Equal("NEW", File.ReadAllText(replacement.FinalPath));
        Assert.Equal(ReplacementStatus.Replaced, (await new OptimisarrDbContext(_options).Replacements.SingleAsync()).Status);
    }

    [Fact]
    public async Task Rollback_serialises_concurrent_attempts_for_the_same_job()
    {
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);
        var replaced = await ReplaceAsync(jobId);
        var replacement = replaced.Replacement!;
        var coordinator = new ReplacementCoordinator();
        var firstMoveEntered = new TaskCompletionSource();
        var releaseFirstMove = new TaskCompletionSource();

        FileMoveResult BlockingMove(string source, string destination)
        {
            firstMoveEntered.TrySetResult();
            releaseFirstMove.Task.GetAwaiter().GetResult();
            return FileMover.Move(source, destination);
        }

        var first = Task.Run(() => RollbackAsync(replacement.Id, BlockingMove, coordinator));
        await firstMoveEntered.Task;
        var second = await RollbackAsync(replacement.Id, coordinator: coordinator);
        releaseFirstMove.SetResult();

        Assert.Equal(ReplacementResultKind.Invalid, second.Kind);
        Assert.Contains("already in progress", second.Message);
        Assert.Equal(ReplacementResultKind.Success, (await first).Kind);
    }

    [Fact]
    public async Task Recovery_restores_a_staged_output_after_an_interrupted_rollback()
    {
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);
        var replaced = await ReplaceAsync(jobId);
        var replacement = replaced.Replacement!;
        var stagedPath = replacement.FinalPath + $".optimisarr-rollback-{replacement.Id}.tmp";
        File.Move(replacement.FinalPath, stagedPath);
        await SetReplacementStatusAsync(replacement.Id, ReplacementStatus.RollbackPending);

        await using var db = new OptimisarrDbContext(_options);
        Assert.Equal(1, await NewService(db).RecoverPendingAsync(CancellationToken.None));

        Assert.Equal("NEW", File.ReadAllText(replacement.FinalPath));
        Assert.False(File.Exists(stagedPath));
        Assert.Equal(ReplacementStatus.Replaced, (await db.Replacements.SingleAsync()).Status);
    }

    [Fact]
    public async Task Recovery_finalizes_a_rollback_when_the_original_was_already_restored()
    {
        var (originalPath, outputPath) = WriteFiles("Movie.avi", "Movie.mkv", "ORIGINAL", "NEW");
        var jobId = await SeedReadyJobAsync(originalPath, outputPath, verificationPassed: true);
        var replaced = await ReplaceAsync(jobId);
        var replacement = replaced.Replacement!;
        var stagedPath = replacement.FinalPath + $".optimisarr-rollback-{replacement.Id}.tmp";
        File.Move(replacement.FinalPath, stagedPath);
        File.Move(replacement.QuarantinePath, replacement.OriginalPath);
        await SetReplacementStatusAsync(replacement.Id, ReplacementStatus.RollbackPending);

        await using var db = new OptimisarrDbContext(_options);
        Assert.Equal(1, await NewService(db).RecoverPendingAsync(CancellationToken.None));

        Assert.Equal("ORIGINAL", File.ReadAllText(originalPath));
        Assert.False(File.Exists(stagedPath));
        Assert.Equal(ReplacementStatus.RolledBack, (await db.Replacements.SingleAsync()).Status);
        Assert.Equal(LifetimeStats.Empty, await ReadLifetimeAsync());
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

    private async Task<ReplacementActionResult> RollbackAsync(
        int replacementId,
        Func<string, string, FileMoveResult>? moveFile = null,
        ReplacementCoordinator? coordinator = null)
    {
        await using var db = new OptimisarrDbContext(_options);
        var service = NewService(db, moveFile: moveFile, coordinator: coordinator);
        return await service.RollbackAsync(replacementId, CancellationToken.None);
    }

    private async Task SeedPendingReplacementAsync(
        int jobId,
        string originalPath,
        string finalPath,
        string quarantinePath)
    {
        await using var db = new OptimisarrDbContext(_options);
        var job = await db.Jobs.SingleAsync(job => job.Id == jobId);
        db.Replacements.Add(new Replacement
        {
            JobId = jobId,
            MediaFileId = job.MediaFileId,
            OriginalPath = originalPath,
            FinalPath = finalPath,
            QuarantinePath = quarantinePath,
            OriginalSizeBytes = "ORIGINAL".Length,
            NewSizeBytes = "NEW".Length,
            Status = ReplacementStatus.Pending
        });
        await db.SaveChangesAsync();
    }

    private async Task SetReplacementStatusAsync(int replacementId, ReplacementStatus status)
    {
        await using var db = new OptimisarrDbContext(_options);
        var replacement = await db.Replacements.SingleAsync(row => row.Id == replacementId);
        replacement.Status = status;
        await db.SaveChangesAsync();
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

    private async Task SetDryRunModeAsync(bool enabled)
    {
        await using var db = new OptimisarrDbContext(_options);
        var settings = new SettingsStore(db);
        var current = await settings.GetQueueSettingsAsync(CancellationToken.None);
        await settings.SetQueueSettingsAsync(current with { DryRunMode = enabled }, CancellationToken.None);
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
