using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

/// <summary>The outcome of excluding one file.</summary>
public sealed record ExclusionOutcome(int MediaFileId, bool Succeeded, string Code, string Message, Exclusion? Exclusion)
{
    public static ExclusionOutcome Ok(int mediaFileId, Exclusion exclusion) =>
        new(mediaFileId, true, string.Empty, string.Empty, exclusion);

    public static ExclusionOutcome Rejected(int mediaFileId, string code, string message) =>
        new(mediaFileId, false, code, message, null);
}

/// <summary>The outcome of removing one exclusion.</summary>
public sealed record ExclusionUndoOutcome(int ExclusionId, bool Succeeded, string Code, string Message, int? MediaFileId)
{
    public static ExclusionUndoOutcome Ok(int exclusionId, int? mediaFileId) =>
        new(exclusionId, true, string.Empty, string.Empty, mediaFileId);

    public static ExclusionUndoOutcome Rejected(int exclusionId, string code, string message) =>
        new(exclusionId, false, code, message, null);
}

/// <summary>
/// The rule for excluding a file from optimisation, shared by the single-file endpoint and the
/// batch endpoint so a batch never excludes a file the single action would refuse. Excluding is
/// idempotent on the file's path: excluding an already-excluded file keeps the original
/// exclusion and its reason rather than replacing it.
/// </summary>
public static class ExclusionOperations
{
    public static async Task<ExclusionOutcome> ApplyAsync(
        OptimisarrDbContext db, int mediaFileId, string? reason, CancellationToken token)
    {
        var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.Id == mediaFileId, token);
        if (file is null)
        {
            return ExclusionOutcome.Rejected(mediaFileId, "media.notFound", $"No media file with id {mediaFileId}.");
        }

        var exclusion = await db.Exclusions.FirstOrDefaultAsync(e => e.Path == file.Path, token);
        if (exclusion is null)
        {
            exclusion = new Exclusion
            {
                Path = file.Path,
                LibraryId = file.LibraryId,
                RelativePath = file.RelativePath,
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                Source = ExclusionSource.Manual
            };
            db.Exclusions.Add(exclusion);
        }

        // Tidy the queue: drop the file's failed/cancelled attempts so excluding from the Queue makes
        // them disappear. A running job is left to finish and a completed one is kept (it may own a
        // replacement); the exclusion is what stops the file being offered again.
        var spentJobs = await db.Jobs
            .Where(job => job.Type == JobType.Normal
                && job.MediaFileId == file.Id
                && (job.Status == JobStatus.Failed || job.Status == JobStatus.Cancelled)
                && !db.Replacements.Any(replacement => replacement.JobId == job.Id))
            .ToListAsync(token);
        db.Jobs.RemoveRange(spentJobs);

        await db.SaveChangesAsync(token);
        return ExclusionOutcome.Ok(mediaFileId, exclusion);
    }

    /// <summary>
    /// Remove an exclusion — the file becomes eligible again under its library's rules. Shared by the
    /// single and batch endpoints, so lifting an exclusion in bulk applies the same rules: the file's
    /// failure streak is reset, otherwise an auto-excluded file would be re-excluded on its next failure.
    /// </summary>
    public static async Task<ExclusionUndoOutcome> UndoAsync(
        OptimisarrDbContext db, int exclusionId, CancellationToken token)
    {
        var exclusion = await db.Exclusions.FirstOrDefaultAsync(e => e.Id == exclusionId, token);
        if (exclusion is null)
        {
            return ExclusionUndoOutcome.Rejected(
                exclusionId, "exclusion.notFound", $"No exclusion with id {exclusionId}.");
        }

        db.Exclusions.Remove(exclusion);

        var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.Path == exclusion.Path, token);
        if (file is not null && file.FailureCount != 0)
        {
            file.FailureCount = 0;
            file.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(token);
        return ExclusionUndoOutcome.Ok(exclusionId, file?.Id);
    }
}
