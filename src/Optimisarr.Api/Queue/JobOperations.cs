using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Api.Queue;

/// <summary>Why one job operation did not complete.</summary>
public enum JobOperationFailure
{
    NotFound,
    InvalidState,
    RecoveryPending,
    WorkCleanupFailed,
    Excluded,
    Reserved,
}

/// <summary>
/// The outcome of one job operation. The single-job endpoints turn a rejection into an HTTP
/// status; the batch endpoints collect the same outcomes so a batch applies exactly the
/// checks one row at a time would.
/// </summary>
public sealed record JobOperationOutcome(
    int Id,
    bool Succeeded,
    JobOperationFailure Failure,
    string Code,
    string Message,
    object? Args = null)
{
    public static JobOperationOutcome Ok(int id) => new(id, true, default, string.Empty, string.Empty);

    public static JobOperationOutcome Rejected(
        int id,
        JobOperationFailure failure,
        string code,
        string message,
        object? args = null) =>
        new(id, false, failure, code, message, args);
}

/// <summary>
/// The cancel, remove and retry rules for a job, shared by the single-job endpoints and the
/// batch endpoints. A batch must never be more permissive than the single action it stands in
/// for, so the checks live here once.
/// </summary>
public static class JobOperations
{
    /// <summary>
    /// Marks a job that is still in flight as cancelled and releases its worker leases, so the
    /// removal below can proceed. The caller stops the running process afterwards; the state
    /// transition itself is all this method does.
    /// </summary>
    public static async Task CancelAsync(OptimisarrDbContext db, Job job, CancellationToken token)
    {
        job.Status = JobStatus.Cancelled;
        job.FinishedAt = DateTimeOffset.UtcNow;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        var leases = await db.JobLeases.Where(lease => lease.JobId == job.Id && lease.State == LeaseState.Held)
            .ToListAsync(token);
        foreach (var lease in leases)
        {
            lease.State = LeaseState.Released;
            lease.EndedAt = job.FinishedAt;
        }
        await db.SaveChangesAsync(token);
    }

    /// <summary>
    /// Removes a job from the queue. A failed or cancelled job is removed outright; an active
    /// job is removed only when <paramref name="cancelActive"/> is set, which stops it first —
    /// the same sequence the Queue's own "Stop &amp; remove" action performs. A completed
    /// replacement is never removed: its job record protects the rollback path.
    /// </summary>
    public static async Task<JobOperationOutcome> RemoveAsync(
        OptimisarrDbContext db, QueueDispatcher dispatcher, int id, bool cancelActive, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id && j.Type == JobType.Normal, token);
        if (job is null)
        {
            return JobOperationOutcome.Rejected(id, JobOperationFailure.NotFound,
                "job.notFound", $"No job with id {id}.");
        }

        if (job.Status is not (JobStatus.Failed or JobStatus.Cancelled))
        {
            if (!cancelActive || !JobEnqueueService.ActiveStatuses.Contains(job.Status))
            {
                return JobOperationOutcome.Rejected(id, JobOperationFailure.InvalidState, "job.remove.active",
                    "Stop an active job before removing it from the queue.");
            }
            await CancelAsync(db, job, token);
            dispatcher.RequestCancel(job.Id);
        }

        if (await db.Replacements.AnyAsync(r => r.JobId == id
            && JobClearing.LiveReplacementStatuses.Contains(r.Status), token))
        {
            return JobOperationOutcome.Rejected(id, JobOperationFailure.RecoveryPending, "job.recoveryPending",
                "This job owns a live or interrupted rollback. Resolve its replacement before retrying or removing it.");
        }

        if (!await dispatcher.TryDiscardWorkOutputAsync(db, job, token))
        {
            return JobOperationOutcome.Rejected(id, JobOperationFailure.WorkCleanupFailed, "job.workCleanupFailed",
                "Cleanup is deferred while the output is reserved, shared, or unavailable. The job and diagnostics were retained; resolve the file operation and retry.");
        }

        db.Jobs.Remove(job);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return JobOperationOutcome.Ok(id);
    }

    /// <summary>
    /// Re-queues a failed or cancelled job so the operator can deliberately try the file again.
    /// The attempt is archived and the adaptive selection is cleared, so the job is re-evaluated
    /// against the current settings rather than replaying the old choice.
    /// </summary>
    public static async Task<JobOperationOutcome> RetryAsync(
        OptimisarrDbContext db, QueueDispatcher dispatcher, int id, bool higherQuality, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id && j.Type == JobType.Normal, token);
        if (job is null)
        {
            return JobOperationOutcome.Rejected(id, JobOperationFailure.NotFound,
                "job.notFound", $"No job with id {id}.");
        }

        if (job.Status is not (JobStatus.Failed or JobStatus.Cancelled))
        {
            return JobOperationOutcome.Rejected(id, JobOperationFailure.InvalidState, "job.retry.invalidState",
                $"Only failed or cancelled jobs can be retried; job {id} is {job.Status}.",
                new { id, status = job.Status.ToString() });
        }

        var excluded = await db.MediaFiles.AsNoTracking()
            .Where(file => file.Id == job.MediaFileId)
            .AnyAsync(file => db.Exclusions.Any(e => e.Path == file.Path), token);
        if (excluded)
        {
            return JobOperationOutcome.Rejected(id, JobOperationFailure.Excluded, "job.retry.excluded",
                "This file is excluded. Remove its exclusion from the library's Excluded tab before retrying.");
        }

        if (await db.Replacements.AnyAsync(r => r.JobId == id
            && JobClearing.LiveReplacementStatuses.Contains(r.Status), token))
        {
            return JobOperationOutcome.Rejected(id, JobOperationFailure.RecoveryPending, "job.recoveryPending",
                "This job owns a live or interrupted rollback. Resolve its replacement before retrying or removing it.");
        }

        // Preserve the admission error contract, but check before discarding any candidate.
        var sourcePath = await db.MediaFiles.AsNoTracking().Where(f => f.Id == job.MediaFileId)
            .Select(f => f.Path).SingleOrDefaultAsync(token);
        if (await db.IsFileReservedAsync(job.MediaFileId,
            new[] { sourcePath, job.WorkOutputPath }.OfType<string>().ToArray(), token))
        {
            return JobOperationOutcome.Rejected(id, JobOperationFailure.Reserved, "dedupe.reserved",
                "This file is reserved by an unresolved duplicate action. Resolve or cancel that action before starting work.");
        }

        if (!await dispatcher.TryDiscardWorkOutputAsync(db, job, token))
        {
            return JobOperationOutcome.Rejected(id, JobOperationFailure.WorkCleanupFailed, "job.workCleanupFailed",
                "Cleanup is deferred while the output is reserved, shared, or unavailable. The job and diagnostics were retained; resolve the file operation and retry.");
        }

        JobAttemptHistory.Archive(job, "ManualRetry", DateTimeOffset.UtcNow);
        job.Status = JobStatus.Queued;
        job.ErrorMessage = null;
        job.FailureCategory = null;
        job.ProcessLog = null;
        // A retry must re-evaluate the samples against the current settings and whichever
        // encoder claims the job. Reusing the old selected quality also skips size preflight.
        job.AdaptiveVideoQuality = null;
        job.AdaptiveAudioBitrateKbps = null;
        job.AdaptiveImageQuality = null;
        job.PerceptualSearchJson = null;
        job.VmafModel = null;
        if (higherQuality)
        {
            job.QualityRetryCount += 1;
        }
        job.Progress = 0;
        job.StartedAt = null;
        job.FinishedAt = null;
        job.WorkOutputPath = null;
        job.OutputSizeBytes = null;
        job.VerificationPassed = null;
        job.SourceSha256 = null;
        job.VerifiedSourceSha256 = null;
        job.VerifiedOutputSha256 = null;
        job.VerificationReportJson = null;
        job.VerifiedAt = null;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);

        dispatcher.Wake();
        return JobOperationOutcome.Ok(id);
    }
}
