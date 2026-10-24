using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Deduplication;
using Optimisarr.Data;

namespace Optimisarr.Api.Queue;

internal enum RuntimeCleanupResult { Discarded, Deferred, Superseded }

/// <summary>Fences stopped-process cleanup and retry transitions against changed attempts.</summary>
internal static class RuntimeOutputCleanup
{
    public static async Task<RuntimeCleanupResult> TryRetryAsync(
        OptimisarrDbContext db, FileOperationClaims claims, int jobId, int executionAttempt,
        string outputPath, string workRoot, Func<string, bool> isDirectoryReserved,
        Action<Job> prepareRetry, CancellationToken token,
        string? rejectedProcessLog = null, string? rejectedError = null)
    {
        RequireWriter(db);
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == jobId, token);
        if (job is null || job.ExecutionAttempt != executionAttempt
            || job.Status is not (JobStatus.Transcoding or JobStatus.Verifying)
            || !SamePath(job.WorkOutputPath, outputPath)) return RuntimeCleanupResult.Superseded;

        var discarded = await TryDiscardAsync(db, claims, job, executionAttempt, workRoot, isDirectoryReserved, token);
        if (discarded == RuntimeCleanupResult.Discarded) prepareRetry(job);
        else
        {
            // A reservation or sharing conflict is not a media failure. Preserve the measured
            // verdict/log and retry budget, and do not run failure tracking or auto-exclusion.
            job.Status = JobStatus.Failed;
            if (rejectedProcessLog is not null) job.ProcessLog = rejectedProcessLog;
            if (rejectedError is not null) job.ErrorMessage = rejectedError;
            var reason = "Automatic retry deferred: the previous output is reserved, shared, or unavailable. "
                + "Files and diagnostics were retained; resolve the conflict and retry the job.";
            job.ErrorMessage = string.IsNullOrEmpty(job.ErrorMessage) ? reason : job.ErrorMessage + "\n" + reason;
            job.FinishedAt = DateTimeOffset.UtcNow;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(token);
        return discarded;
    }

    public static async Task<RuntimeCleanupResult> TryDiscardAsync(
        OptimisarrDbContext db, FileOperationClaims claims, Job job, int executionAttempt,
        string workRoot, Func<string, bool> isDirectoryReserved, CancellationToken token)
    {
        RequireWriter(db);
        if (job.ExecutionAttempt != executionAttempt) return RuntimeCleanupResult.Superseded;
        if (job.WorkOutputPath is not { Length: > 0 } path) return RuntimeCleanupResult.Discarded;
        if (!WorkPaths.IsUnderRoot(workRoot, path)
            || !await QueueOutputCleanup.TryDiscardAsync(db, claims, job, workRoot, isDirectoryReserved, token,
                owningProcessStopped: true)) return RuntimeCleanupResult.Deferred;
        job.WorkOutputPath = null;
        return RuntimeCleanupResult.Discarded;
    }

    private static bool SamePath(string? current, string expected) => current is not null
        && string.Equals(Path.GetFullPath(current), Path.GetFullPath(expected),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static void RequireWriter(OptimisarrDbContext db)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Runtime cleanup requires the writer through its job transition and commit.");
    }
}
