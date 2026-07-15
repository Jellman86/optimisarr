using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Api.Queue;
using Optimisarr.Api.Stats;
using Optimisarr.Core.Replacement;
using Optimisarr.Data;
using ReplacementEntity = Optimisarr.Data.Replacement;

namespace Optimisarr.Api.Replacement;

public enum ReplacementResultKind
{
    Success,
    NotFound,
    Invalid,
    Failed
}

public sealed record ReplacementActionResult(
    ReplacementResultKind Kind,
    string? Message,
    ReplacementEntity? Replacement,
    // A permanent failure can never succeed on a later retry (the verified output is gone, the
    // original is gone, or a different optimised file permanently occupies the destination). The
    // dispatcher fails such a job once rather than reconciling it on every cycle forever.
    bool Permanent = false)
{
    public static ReplacementActionResult Ok(ReplacementEntity replacement) =>
        new(ReplacementResultKind.Success, null, replacement);

    public static ReplacementActionResult NotFound(string message) =>
        new(ReplacementResultKind.NotFound, message, null);

    public static ReplacementActionResult Invalid(string message) =>
        new(ReplacementResultKind.Invalid, message, null);

    public static ReplacementActionResult Failed(string message, bool permanent = false) =>
        new(ReplacementResultKind.Failed, message, null, permanent);
}

/// <summary>
/// Performs the only destructive step in Optimisarr — putting a verified output in
/// place of an original — and makes it reversible. The original is moved to
/// quarantine <em>first</em>, then the output is moved into place; a recorded
/// <see cref="Data.Replacement"/> is the rollback path. If any step fails the
/// original is restored from quarantine, so a failure never loses data.
/// </summary>
public sealed class ReplacementService
{
    private readonly OptimisarrDbContext _db;
    private readonly LibraryInventoryService _inventory;
    private readonly ILogger<ReplacementService> _logger;
    private readonly SettingsStore _settings;
    private readonly string _trashRoot;
    private readonly string? _workRoot;
    private readonly Func<string, string, bool> _canMoveAtomically;
    private readonly Func<string, string, FileMoveResult> _moveFile;
    private readonly LibraryRefreshService? _refresh;
    private readonly NotificationService? _notifications;
    private readonly LifetimeStatsStore _lifetime;
    private readonly ReplacementCoordinator _coordinator;

    public ReplacementService(
        OptimisarrDbContext db,
        LibraryInventoryService inventory,
        SettingsStore settings,
        IHostEnvironment environment,
        LibraryRefreshService refresh,
        NotificationService notifications,
        ReplacementCoordinator coordinator,
        ILogger<ReplacementService> logger)
        : this(db, inventory, settings, TrashPaths.Resolve(environment), logger,
            refresh: refresh, notifications: notifications, workRoot: WorkPaths.Resolve(environment),
            coordinator: coordinator)
    {
    }

    // Test seam: lets the suite point the trash root at a temp directory. The library
    // refresh and notifications are optional so tests need not stand up an HTTP stack;
    // moveFile lets a test simulate a mid-move failure to exercise the restore path. The
    // coordinator defaults to a private instance so a test that does not exercise concurrency
    // need not supply one.
    internal ReplacementService(
        OptimisarrDbContext db,
        LibraryInventoryService inventory,
        SettingsStore settings,
        string trashRoot,
        ILogger<ReplacementService> logger,
        Func<string, string, bool>? canMoveAtomically = null,
        LibraryRefreshService? refresh = null,
        NotificationService? notifications = null,
        string? workRoot = null,
        Func<string, string, FileMoveResult>? moveFile = null,
        ReplacementCoordinator? coordinator = null)
    {
        _db = db;
        _inventory = inventory;
        _settings = settings;
        _trashRoot = trashRoot;
        _workRoot = workRoot;
        _logger = logger;
        _canMoveAtomically = canMoveAtomically ?? FileMover.CanMoveAtomically;
        _moveFile = moveFile ?? FileMover.Move;
        _refresh = refresh;
        _notifications = notifications;
        _lifetime = new LifetimeStatsStore(db);
        _coordinator = coordinator ?? new ReplacementCoordinator();
    }

    public async Task<ReplacementActionResult> ReplaceAsync(int jobId, CancellationToken cancellationToken)
    {
        // Only one replacement may act on a job at a time. A job becomes replaceable the instant it
        // reaches ReadyToReplace, where the post-verify auto-replace, the reconcile sweep, and a
        // manual replace can all target it at once; overlapping runs corrupt each other's moves and
        // destroy the verified output (the original is still safely restored). The loser of the claim
        // backs off and lets the winner finish.
        if (!_coordinator.TryBegin(jobId))
        {
            return ReplacementActionResult.Invalid($"A replacement for job {jobId} is already in progress.");
        }

        try
        {
            return await ReplaceCoreAsync(jobId, cancellationToken);
        }
        finally
        {
            _coordinator.End(jobId);
        }
    }

    private async Task<ReplacementActionResult> ReplaceCoreAsync(int jobId, CancellationToken cancellationToken)
    {
        var job = await _db.Jobs
            .Include(j => j.MediaFile)
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

        if (job is null)
        {
            return ReplacementActionResult.NotFound($"No job with id {jobId}.");
        }

        if (job.Status != JobStatus.ReadyToReplace || job.VerificationPassed != true)
        {
            return ReplacementActionResult.Invalid(
                $"Job {jobId} is {job.Status} and has not passed verification; it cannot replace the original.");
        }

        if (job.MediaFile is not { } media)
        {
            return ReplacementActionResult.Invalid($"Job {jobId} has no media file to replace.");
        }

        var settings = await _settings.GetQueueSettingsAsync(cancellationToken);
        if (settings.DryRunMode)
        {
            return ReplacementActionResult.Invalid(
                "Dry-run mode is enabled, so Optimisarr will stop after verification and leave originals untouched.");
        }

        if (string.IsNullOrEmpty(job.WorkOutputPath) || !File.Exists(job.WorkOutputPath))
        {
            return ReplacementActionResult.Failed(
                "The verified output is missing from the work directory.", permanent: true);
        }

        if (!File.Exists(media.Path))
        {
            return ReplacementActionResult.Failed(
                $"The original file no longer exists: {media.Path}", permanent: true);
        }

        var plan = ReplacementPlanner.Plan(
            media.Path,
            job.WorkOutputPath,
            _trashRoot,
            DateTimeOffset.UtcNow,
            $"job-{job.Id}");

        // A transcode that changes the container lands at a new path (e.g. photo.bmp -> photo.webp).
        // If a *different* file already occupies that path — typically another source that optimised
        // to the same name — replacing would overwrite it. Fail safely before quarantining anything,
        // so the original is left untouched. (An unchanged-container replacement lands back on the
        // original's own path, which is expected and not a collision.)
        if (!string.Equals(plan.FinalPath, media.Path, StringComparison.Ordinal) && File.Exists(plan.FinalPath))
        {
            return ReplacementActionResult.Failed(
                $"Replacement would collide with an existing file at {plan.FinalPath}. " +
                "Another optimised file already occupies that path, so the original was left untouched.",
                permanent: true);
        }

        // Replacement moves the original into quarantine and the optimised file into the
        // original's folder, so that folder must be writable by the container's user. Check up
        // front and fail with a clear, actionable message rather than a raw 500 mid-move — the
        // original is still untouched at this point.
        var mediaDirectory = Path.GetDirectoryName(media.Path) ?? string.Empty;
        if (!PathAccessProbe.CanWrite(mediaDirectory))
        {
            return ReplacementActionResult.Invalid(
                $"Optimisarr can't write to the library folder '{mediaDirectory}'. Replacement needs write "
                + "access to move the original into quarantine and the optimised file into its place. Give the "
                + "container's user (PUID/PGID) write permission to your media path, then try again. The original "
                + "was left untouched. (The Libraries page has a 'Test access' check that flags this in advance.)");
        }

        if (!settings.ReplacementAllowCrossFilesystem)
        {
            var originalDirectory = Path.GetDirectoryName(plan.OriginalPath) ?? string.Empty;
            var quarantineDirectory = Path.GetDirectoryName(plan.QuarantinePath) ?? _trashRoot;
            var outputDirectory = Path.GetDirectoryName(job.WorkOutputPath) ?? string.Empty;
            var finalDirectory = Path.GetDirectoryName(plan.FinalPath) ?? originalDirectory;

            if (!_canMoveAtomically(originalDirectory, quarantineDirectory)
                || !_canMoveAtomically(outputDirectory, finalDirectory))
            {
                return ReplacementActionResult.Invalid(
                    "Replacement would require a cross-filesystem copy-plus-delete move. Enable cross-filesystem replacement in Settings if this mount layout is intentional.");
            }
        }

        var originalSize = new FileInfo(media.Path).Length;
        var outputSize = new FileInfo(job.WorkOutputPath).Length;

        // Record the rollback path durably before the first filesystem mutation. If the process
        // stops at any later instruction, startup recovery can restore the quarantined original or
        // finalize a completed pair of moves from this Pending row.
        var replacement = new ReplacementEntity
        {
            JobId = job.Id,
            MediaFileId = media.Id,
            OriginalPath = plan.OriginalPath,
            QuarantinePath = plan.QuarantinePath,
            FinalPath = plan.FinalPath,
            OriginalSizeBytes = originalSize,
            NewSizeBytes = outputSize,
            Status = ReplacementStatus.Pending,
            ReplacedAt = DateTimeOffset.UtcNow
        };
        _db.Replacements.Add(replacement);
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            // Step 1: preserve the original by quarantining it. From here the original is safe and
            // every ordinary failure restores it; a process crash is handled by Pending recovery.
            var quarantineMove = _moveFile(media.Path, plan.QuarantinePath);

            // Step 2: move the verified output into the original's place.
            var move = _moveFile(job.WorkOutputPath, plan.FinalPath);
            replacement.CrossFilesystem = quarantineMove.CrossFilesystem || move.CrossFilesystem;

            // Step 3: a final-path integrity check — the placed file must exist and
            // match the output we verified, or we do not trust the replacement.
            if (!File.Exists(plan.FinalPath) || new FileInfo(plan.FinalPath).Length != outputSize)
            {
                throw new IOException("The replaced file is missing or its size does not match the verified output.");
            }
        }
        catch (Exception ex)
        {
            var restored = RestoreFromQuarantine(plan, media.Path);
            if (restored)
            {
                _db.Replacements.Remove(replacement);
                await _db.SaveChangesAsync(cancellationToken);
            }

            _logger.LogError(
                ex,
                restored
                    ? "Replacement of job {JobId} failed; original restored from quarantine"
                    : "Replacement of job {JobId} failed; pending rollback record retained for startup recovery",
                jobId);
            var recovery = restored
                ? "the original was restored"
                : "the original remains protected by the recorded pending rollback path";
            return ReplacementActionResult.Failed($"Replacement failed and {recovery}: {ex.Message}");
        }

        replacement.Status = ReplacementStatus.Replaced;

        media.Path = plan.FinalPath;
        media.SizeBytes = outputSize;
        media.UpdatedAt = DateTimeOffset.UtcNow;

        job.Status = JobStatus.Completed;
        job.UpdatedAt = DateTimeOffset.UtcNow;

        // Accrue the durable lifetime savings tally in the same transaction as the replacement,
        // so the Dashboard headline reflects this file and survives later row/history changes.
        await _lifetime.ApplyReplacementAsync(originalSize, outputSize, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        // The verified output moved out of /work into the original's place; tidy the now-empty
        // per-media-file scratch directory it came from.
        if (_workRoot is not null)
        {
            WorkPaths.PruneEmptyAncestors(_workRoot, job.WorkOutputPath!);
        }

        // Refresh the inventory from the file now living at the original's location.
        // Best effort: the replacement is already committed and must not be undone
        // just because a re-probe could not run.
        await TryReprobeAsync(media.Id, cancellationToken);

        // Best effort: tell connected media servers to re-scan the new file.
        await TryRefreshLibrariesAsync(media.Path, cancellationToken);

        // Best effort: notify configured targets of the replacement.
        if (_notifications is not null)
        {
            try
            {
                await _notifications.NotifyReplacementAsync(media.Path, originalSize, outputSize, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Replacement notification for {Path} failed", media.Path);
            }
        }

        return ReplacementActionResult.Ok(replacement);
    }

    /// <summary>
    /// Reconciles interrupted replacement and rollback operations from their durably recorded
    /// intent. Ambiguous states retain the record and are logged rather than guessed destructively.
    /// </summary>
    public async Task<int> RecoverPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await _db.Replacements
            .Include(replacement => replacement.Job)
            .Where(replacement => replacement.Status == ReplacementStatus.Pending
                || replacement.Status == ReplacementStatus.RollbackPending)
            .ToListAsync(cancellationToken);
        var recovered = 0;

        foreach (var replacement in pending)
        {
            if (replacement.Status == ReplacementStatus.RollbackPending)
            {
                recovered += await RecoverPendingRollbackAsync(replacement, cancellationToken) ? 1 : 0;
            }
            else
            {
                recovered += await RecoverPendingReplacementAsync(replacement, cancellationToken) ? 1 : 0;
            }
        }

        return recovered;
    }

    public async Task<ReplacementActionResult> RollbackAsync(int replacementId, CancellationToken cancellationToken)
    {
        var replacement = await _db.Replacements
            .FirstOrDefaultAsync(r => r.Id == replacementId, cancellationToken);

        if (replacement is null)
        {
            return ReplacementActionResult.NotFound($"No replacement with id {replacementId}.");
        }

        if (!_coordinator.TryBegin(replacement.JobId))
        {
            return ReplacementActionResult.Invalid(
                $"A replacement or rollback for job {replacement.JobId} is already in progress.");
        }

        try
        {
            return await RollbackCoreAsync(replacement, cancellationToken);
        }
        finally
        {
            _coordinator.End(replacement.JobId);
        }
    }

    private async Task<ReplacementActionResult> RollbackCoreAsync(
        ReplacementEntity replacement,
        CancellationToken cancellationToken)
    {
        var replacementId = replacement.Id;

        if (replacement.Status != ReplacementStatus.Replaced)
        {
            return ReplacementActionResult.Invalid($"Replacement {replacementId} is already {replacement.Status}.");
        }

        if (!File.Exists(replacement.QuarantinePath))
        {
            return ReplacementActionResult.Failed(
                $"The quarantined original is missing, so it cannot be restored: {replacement.QuarantinePath}");
        }

        replacement.Status = ReplacementStatus.RollbackPending;
        await _db.SaveChangesAsync(cancellationToken);

        string? stagedOutputPath = null;
        try
        {
            // Stage the optimised output instead of deleting it. If restoring the original fails,
            // put this known-good file back so the library never loses its live copy.
            if (File.Exists(replacement.FinalPath))
            {
                stagedOutputPath = RollbackStagingPath(replacement);
                _moveFile(replacement.FinalPath, stagedOutputPath);
            }

            try
            {
                _moveFile(replacement.QuarantinePath, replacement.OriginalPath);
            }
            catch
            {
                RestoreStagedOutput(stagedOutputPath, replacement.FinalPath);
                throw;
            }

        }
        catch (Exception ex)
        {
            if (File.Exists(replacement.FinalPath)
                && File.Exists(replacement.QuarantinePath)
                && RemoveFailedRollbackOriginalCopy(replacement))
            {
                replacement.Status = ReplacementStatus.Replaced;
                await _db.SaveChangesAsync(cancellationToken);
            }

            _logger.LogError(ex, "Rollback of replacement {ReplacementId} failed", replacementId);
            return ReplacementActionResult.Failed($"Rollback failed: {ex.Message}");
        }

        var media = await _db.MediaFiles.FirstOrDefaultAsync(f => f.Id == replacement.MediaFileId, cancellationToken);
        if (media is not null)
        {
            media.Path = replacement.OriginalPath;
            media.SizeBytes = replacement.OriginalSizeBytes;
            media.UpdatedAt = DateTimeOffset.UtcNow;
        }

        replacement.Status = ReplacementStatus.RolledBack;
        replacement.RolledBackAt = DateTimeOffset.UtcNow;

        // The original is back in place, so this replacement saved nothing: reverse its
        // contribution to the lifetime tally in the same transaction.
        await _lifetime.ApplyRollbackAsync(replacement.OriginalSizeBytes, replacement.NewSizeBytes, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        TryDeleteStagedOutput(stagedOutputPath);

        if (media is not null)
        {
            await TryReprobeAsync(media.Id, cancellationToken);
        }

        // Best effort: the restored original is back in place; have servers re-scan it.
        await TryRefreshLibrariesAsync(replacement.OriginalPath, cancellationToken);

        return ReplacementActionResult.Ok(replacement);
    }

    private async Task TryRefreshLibrariesAsync(string changedPath, CancellationToken cancellationToken)
    {
        if (_refresh is null)
        {
            return;
        }

        try
        {
            await _refresh.RefreshForPathAsync(changedPath, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Library refresh after change to {Path} failed", changedPath);
        }
    }

    private async Task TryReprobeAsync(int mediaFileId, CancellationToken cancellationToken)
    {
        try
        {
            await _inventory.ProbeAsync(mediaFileId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Re-probe of media file {MediaFileId} after replacement failed", mediaFileId);
        }
    }

    private bool RestoreFromQuarantine(ReplacementPlan plan, string originalPath)
    {
        // The original is only safe to restore if it actually reached quarantine. If the
        // quarantine move itself failed, the original is still at its own path — leave it be.
        if (!File.Exists(plan.QuarantinePath))
        {
            return File.Exists(originalPath);
        }

        try
        {
            // A failed output move can leave a partial or complete output behind — including at the
            // original's own path when the container is unchanged (FinalPath == originalPath). That
            // remnant is disposable (the output is reproducible from /work) and must never be mistaken
            // for the protected original, so clear both possible locations before restoring. Guarding
            // the restore on "originalPath is empty" alone would skip it when such a remnant sits there,
            // stranding the original in quarantine.
            if (File.Exists(originalPath))
            {
                File.Delete(originalPath);
            }
            if (!string.Equals(plan.FinalPath, originalPath, StringComparison.Ordinal) && File.Exists(plan.FinalPath))
            {
                File.Delete(plan.FinalPath);
            }

            FileMover.Move(plan.QuarantinePath, originalPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Could not restore original from quarantine {Quarantine} to {Original}; the original is preserved in quarantine",
                plan.QuarantinePath, originalPath);
            return false;
        }
    }

    private async Task<bool> RecoverPendingReplacementAsync(
        ReplacementEntity replacement,
        CancellationToken cancellationToken)
    {
        var workOutputExists = replacement.Job?.WorkOutputPath is { Length: > 0 } workOutputPath
            && File.Exists(workOutputPath);
        var quarantineExists = File.Exists(replacement.QuarantinePath);
        var finalIsComplete = FileHasLength(replacement.FinalPath, replacement.NewSizeBytes);

        if (quarantineExists && !workOutputExists && finalIsComplete)
        {
            await FinalizeRecoveredReplacementAsync(replacement, cancellationToken);
            return true;
        }

        if (quarantineExists)
        {
            var plan = new ReplacementPlan(
                replacement.OriginalPath,
                replacement.FinalPath,
                replacement.QuarantinePath);
            if (RestoreFromQuarantine(plan, replacement.OriginalPath))
            {
                _db.Replacements.Remove(replacement);
                await _db.SaveChangesAsync(cancellationToken);
                _logger.LogWarning(
                    "Restored original for interrupted pending replacement {ReplacementId}",
                    replacement.Id);
                return true;
            }
        }
        else if (File.Exists(replacement.OriginalPath) && workOutputExists)
        {
            // The process stopped after recording Pending but before moving the original.
            _db.Replacements.Remove(replacement);
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        LogUnrecoverablePending(replacement);
        return false;
    }

    private async Task<bool> RecoverPendingRollbackAsync(
        ReplacementEntity replacement,
        CancellationToken cancellationToken)
    {
        var stagedOutputPath = RollbackStagingPath(replacement);
        var quarantineExists = File.Exists(replacement.QuarantinePath);

        if (!quarantineExists && FileHasLength(replacement.OriginalPath, replacement.OriginalSizeBytes))
        {
            await FinalizeRecoveredRollbackAsync(replacement, cancellationToken);
            TryDeleteStagedOutput(stagedOutputPath);
            return true;
        }

        if (quarantineExists
            && File.Exists(replacement.FinalPath)
            && RemoveFailedRollbackOriginalCopy(replacement))
        {
            replacement.Status = ReplacementStatus.Replaced;
            await _db.SaveChangesAsync(cancellationToken);
            TryDeleteStagedOutput(stagedOutputPath);
            _logger.LogWarning(
                "Restored replacement state for interrupted rollback {ReplacementId}",
                replacement.Id);
            return true;
        }

        if (quarantineExists && File.Exists(stagedOutputPath))
        {
            try
            {
                _moveFile(stagedOutputPath, replacement.FinalPath);
                replacement.Status = ReplacementStatus.Replaced;
                await _db.SaveChangesAsync(cancellationToken);
                _logger.LogWarning(
                    "Restored staged optimised output for interrupted rollback {ReplacementId}",
                    replacement.Id);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(
                    ex,
                    "Could not restore staged output for pending rollback {ReplacementId}",
                    replacement.Id);
                return false;
            }
        }

        if (quarantineExists)
        {
            // The optimised file is no longer available. Restore the protected original rather
            // than leave the library path empty, then commit the rollback state.
            var plan = new ReplacementPlan(
                replacement.OriginalPath,
                replacement.FinalPath,
                replacement.QuarantinePath);
            if (RestoreFromQuarantine(plan, replacement.OriginalPath))
            {
                await FinalizeRecoveredRollbackAsync(replacement, cancellationToken);
                return true;
            }
        }

        LogUnrecoverablePending(replacement);
        return false;
    }

    private async Task FinalizeRecoveredRollbackAsync(
        ReplacementEntity replacement,
        CancellationToken cancellationToken)
    {
        var media = await _db.MediaFiles
            .FirstOrDefaultAsync(file => file.Id == replacement.MediaFileId, cancellationToken);
        if (media is not null)
        {
            media.Path = replacement.OriginalPath;
            media.SizeBytes = replacement.OriginalSizeBytes;
            media.UpdatedAt = DateTimeOffset.UtcNow;
        }

        replacement.Status = ReplacementStatus.RolledBack;
        replacement.RolledBackAt = DateTimeOffset.UtcNow;
        await _lifetime.ApplyRollbackAsync(
            replacement.OriginalSizeBytes,
            replacement.NewSizeBytes,
            cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogWarning(
            "Finalized interrupted rollback {ReplacementId}; original was already restored",
            replacement.Id);
    }

    private async Task FinalizeRecoveredReplacementAsync(
        ReplacementEntity replacement,
        CancellationToken cancellationToken)
    {
        var media = await _db.MediaFiles
            .FirstOrDefaultAsync(file => file.Id == replacement.MediaFileId, cancellationToken);
        if (media is not null)
        {
            media.Path = replacement.FinalPath;
            media.SizeBytes = replacement.NewSizeBytes;
            media.UpdatedAt = DateTimeOffset.UtcNow;
        }

        if (replacement.Job is not null)
        {
            replacement.Job.Status = JobStatus.Completed;
            replacement.Job.UpdatedAt = DateTimeOffset.UtcNow;
        }

        replacement.Status = ReplacementStatus.Replaced;
        await _lifetime.ApplyReplacementAsync(
            replacement.OriginalSizeBytes,
            replacement.NewSizeBytes,
            cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogWarning(
            "Finalized interrupted replacement {ReplacementId}; verified output was already in place",
            replacement.Id);
    }

    private void RestoreStagedOutput(string? stagedOutputPath, string finalPath)
    {
        if (stagedOutputPath is null || !File.Exists(stagedOutputPath) || File.Exists(finalPath))
        {
            return;
        }

        try
        {
            _moveFile(stagedOutputPath, finalPath);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(
                ex,
                "Could not restore staged optimised output {StagedOutput} to {FinalPath}",
                stagedOutputPath,
                finalPath);
        }
    }

    private void TryDeleteStagedOutput(string? stagedOutputPath)
    {
        if (stagedOutputPath is null)
        {
            return;
        }

        try
        {
            File.Delete(stagedOutputPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove staged rollback output {Path}", stagedOutputPath);
        }
    }

    private static string RollbackStagingPath(ReplacementEntity replacement) =>
        replacement.FinalPath + $".optimisarr-rollback-{replacement.Id}.tmp";

    private bool RemoveFailedRollbackOriginalCopy(ReplacementEntity replacement)
    {
        if (string.Equals(replacement.OriginalPath, replacement.FinalPath, StringComparison.Ordinal)
            || !File.Exists(replacement.OriginalPath))
        {
            return true;
        }

        try
        {
            // A failed cross-filesystem restore can leave a verified copy at OriginalPath while the
            // protected source still exists in quarantine. Prove the two files are identical before
            // removing the duplicate; an unrelated file that appeared at the path is never touched.
            FileMover.VerifyCopiedContent(replacement.QuarantinePath, replacement.OriginalPath);
            File.Delete(replacement.OriginalPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogCritical(
                ex,
                "Could not remove partial rollback copy {OriginalPath} for replacement {ReplacementId}",
                replacement.OriginalPath,
                replacement.Id);
            return false;
        }
    }

    private void LogUnrecoverablePending(ReplacementEntity replacement)
    {
        _logger.LogCritical(
            "Pending {Status} operation {ReplacementId} could not be recovered automatically. Original={Original}; Quarantine={Quarantine}; Final={Final}",
            replacement.Status,
            replacement.Id,
            replacement.OriginalPath,
            replacement.QuarantinePath,
            replacement.FinalPath);
    }

    private static bool FileHasLength(string path, long expectedLength)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length == expectedLength;
        }
        catch (IOException)
        {
            return false;
        }
    }

}
