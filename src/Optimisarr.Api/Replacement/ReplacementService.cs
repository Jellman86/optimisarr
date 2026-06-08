using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
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
    private readonly Func<string, string, bool> _canMoveAtomically;

    public ReplacementService(
        OptimisarrDbContext db,
        LibraryInventoryService inventory,
        SettingsStore settings,
        IHostEnvironment environment,
        ILogger<ReplacementService> logger)
        : this(db, inventory, settings, ResolveTrashRoot(environment), logger)
    {
    }

    // Test seam: lets the suite point the trash root at a temp directory.
    internal ReplacementService(
        OptimisarrDbContext db,
        LibraryInventoryService inventory,
        SettingsStore settings,
        string trashRoot,
        ILogger<ReplacementService> logger,
        Func<string, string, bool>? canMoveAtomically = null)
    {
        _db = db;
        _inventory = inventory;
        _settings = settings;
        _trashRoot = trashRoot;
        _logger = logger;
        _canMoveAtomically = canMoveAtomically ?? FileMover.CanMoveAtomically;
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
        var quarantineMove = FileMover.Move(media.Path, plan.QuarantinePath);

        bool crossFilesystem;
        try
        {
            // Step 2: move the verified output into the original's place.
            var move = FileMover.Move(job.WorkOutputPath, plan.FinalPath);
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
        await _db.SaveChangesAsync(cancellationToken);

        // Refresh the inventory from the file now living at the original's location.
        // Best effort: the replacement is already committed and must not be undone
        // just because a re-probe could not run.
        await TryReprobeAsync(media.Id, cancellationToken);

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
        await _db.SaveChangesAsync(cancellationToken);

        if (media is not null)
        {
            await TryReprobeAsync(media.Id, cancellationToken);
        }

        return ReplacementActionResult.Ok(replacement);
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
        try
        {
            if (!File.Exists(originalPath) && File.Exists(plan.QuarantinePath))
            {
                FileMover.Move(plan.QuarantinePath, originalPath);
            }
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
