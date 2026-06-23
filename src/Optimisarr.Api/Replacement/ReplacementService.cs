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
    ReplacementEntity? Replacement)
{
    public static ReplacementActionResult Ok(ReplacementEntity replacement) =>
        new(ReplacementResultKind.Success, null, replacement);

    public static ReplacementActionResult NotFound(string message) =>
        new(ReplacementResultKind.NotFound, message, null);

    public static ReplacementActionResult Invalid(string message) =>
        new(ReplacementResultKind.Invalid, message, null);

    public static ReplacementActionResult Failed(string message) =>
        new(ReplacementResultKind.Failed, message, null);
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

    public ReplacementService(
        OptimisarrDbContext db,
        LibraryInventoryService inventory,
        SettingsStore settings,
        IHostEnvironment environment,
        LibraryRefreshService refresh,
        NotificationService notifications,
        ILogger<ReplacementService> logger)
        : this(db, inventory, settings, ResolveTrashRoot(environment), logger,
            refresh: refresh, notifications: notifications, workRoot: WorkPaths.Resolve(environment))
    {
    }

    // Test seam: lets the suite point the trash root at a temp directory. The library
    // refresh and notifications are optional so tests need not stand up an HTTP stack;
    // moveFile lets a test simulate a mid-move failure to exercise the restore path.
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
        Func<string, string, FileMoveResult>? moveFile = null)
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
    }

    public async Task<ReplacementActionResult> ReplaceAsync(int jobId, CancellationToken cancellationToken)
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

        if (string.IsNullOrEmpty(job.WorkOutputPath) || !File.Exists(job.WorkOutputPath))
        {
            return ReplacementActionResult.Failed("The verified output is missing from the work directory.");
        }

        if (!File.Exists(media.Path))
        {
            return ReplacementActionResult.Failed($"The original file no longer exists: {media.Path}");
        }

        var plan = ReplacementPlanner.Plan(media.Path, job.WorkOutputPath, _trashRoot, DateTimeOffset.UtcNow);

        // A transcode that changes the container lands at a new path (e.g. photo.bmp -> photo.webp).
        // If a *different* file already occupies that path — typically another source that optimised
        // to the same name — replacing would overwrite it. Fail safely before quarantining anything,
        // so the original is left untouched. (An unchanged-container replacement lands back on the
        // original's own path, which is expected and not a collision.)
        if (!string.Equals(plan.FinalPath, media.Path, StringComparison.Ordinal) && File.Exists(plan.FinalPath))
        {
            return ReplacementActionResult.Failed(
                $"Replacement would collide with an existing file at {plan.FinalPath}. " +
                "Another optimised file already occupies that path, so the original was left untouched.");
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

        var settings = await _settings.GetQueueSettingsAsync(cancellationToken);
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

        // Step 1: preserve the original by quarantining it. From here the original
        // is safe and every subsequent failure restores it.
        var quarantineMove = _moveFile(media.Path, plan.QuarantinePath);

        bool crossFilesystem;
        try
        {
            // Step 2: move the verified output into the original's place.
            var move = _moveFile(job.WorkOutputPath, plan.FinalPath);
            crossFilesystem = quarantineMove.CrossFilesystem || move.CrossFilesystem;

            // Step 3: a final-path integrity check — the placed file must exist and
            // match the output we verified, or we do not trust the replacement.
            if (!File.Exists(plan.FinalPath) || new FileInfo(plan.FinalPath).Length != outputSize)
            {
                throw new IOException("The replaced file is missing or its size does not match the verified output.");
            }
        }
        catch (Exception ex)
        {
            RestoreFromQuarantine(plan, media.Path);
            _logger.LogError(ex, "Replacement of job {JobId} failed; original restored from quarantine", jobId);
            return ReplacementActionResult.Failed($"Replacement failed and the original was restored: {ex.Message}");
        }

        var replacement = new ReplacementEntity
        {
            JobId = job.Id,
            MediaFileId = media.Id,
            OriginalPath = plan.OriginalPath,
            QuarantinePath = plan.QuarantinePath,
            FinalPath = plan.FinalPath,
            OriginalSizeBytes = originalSize,
            NewSizeBytes = outputSize,
            CrossFilesystem = crossFilesystem,
            Status = ReplacementStatus.Replaced,
            ReplacedAt = DateTimeOffset.UtcNow
        };
        _db.Replacements.Add(replacement);

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

    public async Task<ReplacementActionResult> RollbackAsync(int replacementId, CancellationToken cancellationToken)
    {
        var replacement = await _db.Replacements
            .FirstOrDefaultAsync(r => r.Id == replacementId, cancellationToken);

        if (replacement is null)
        {
            return ReplacementActionResult.NotFound($"No replacement with id {replacementId}.");
        }

        if (replacement.Status != ReplacementStatus.Replaced)
        {
            return ReplacementActionResult.Invalid($"Replacement {replacementId} is already {replacement.Status}.");
        }

        if (!File.Exists(replacement.QuarantinePath))
        {
            return ReplacementActionResult.Failed(
                $"The quarantined original is missing, so it cannot be restored: {replacement.QuarantinePath}");
        }

        try
        {
            // Remove the replacement output (it is disposable; the original is the
            // file we must protect), then restore the original from quarantine.
            if (File.Exists(replacement.FinalPath))
            {
                File.Delete(replacement.FinalPath);
            }

            FileMover.Move(replacement.QuarantinePath, replacement.OriginalPath);
        }
        catch (Exception ex)
        {
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

    private void RestoreFromQuarantine(ReplacementPlan plan, string originalPath)
    {
        // The original is only safe to restore if it actually reached quarantine. If the
        // quarantine move itself failed, the original is still at its own path — leave it be.
        if (!File.Exists(plan.QuarantinePath))
        {
            return;
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Could not restore original from quarantine {Quarantine} to {Original}; the original is preserved in quarantine",
                plan.QuarantinePath, originalPath);
        }
    }

    private static string ResolveTrashRoot(IHostEnvironment environment)
    {
        var configured = Environment.GetEnvironmentVariable("OPTIMISARR_TRASH_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return Directory.Exists("/trash")
            ? "/trash"
            : Path.Combine(environment.ContentRootPath, "trash");
    }
}
