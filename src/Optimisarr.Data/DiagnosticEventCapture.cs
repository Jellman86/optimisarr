using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Diagnostics;

namespace Optimisarr.Data;

/// <summary>Stages bounded diagnostic events in the same transaction as their job state.</summary>
public static class DiagnosticEventCapture
{
    public static async Task<bool> AppendJobTransitionAsync(
        OptimisarrDbContext db,
        Job job,
        JobStatus previousStatus,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (job.Status == previousStatus)
        {
            return false;
        }

        var sessions = await db.DiagnosticCaptureSessions
            .Where(session => session.StoppedAt == null
                && (session.ScopedJobId == null || session.ScopedJobId == job.Id))
            .ToListAsync(cancellationToken);
        var session = sessions.FirstOrDefault(candidate => DiagnosticCapturePolicy.AllowsEvent(
            candidate.StartedAt, candidate.ExpiresAt, candidate.StoppedAt,
            candidate.ScopedJobId, job.Id, nowUtc));
        if (session is null)
        {
            return false;
        }

        if (session.EventsStored >= DiagnosticCapturePolicy.MaximumEvents)
        {
            session.EventLimitReached = true;
            return false;
        }

        JobLease? lease = null;
        if (job.Status is JobStatus.Leased or JobStatus.AwaitingVerification
            || previousStatus is JobStatus.Leased or JobStatus.AwaitingVerification)
        {
            lease = db.ChangeTracker.Entries<JobLease>()
                .Where(entry => entry.State != EntityState.Deleted && entry.Entity.JobId == job.Id)
                .Select(entry => entry.Entity)
                .OrderByDescending(candidate => candidate.AcquiredAt)
                .FirstOrDefault();
            lease ??= (await db.JobLeases.AsNoTracking()
                    .Where(candidate => candidate.JobId == job.Id)
                    .ToListAsync(cancellationToken))
                .OrderByDescending(candidate => candidate.AcquiredAt)
                .FirstOrDefault();
        }

        db.DiagnosticEvents.Add(new DiagnosticEvent
        {
            SessionId = session.Id,
            OccurredAt = nowUtc,
            JobId = job.Id,
            Attempt = job.ExecutionAttempt,
            LeaseId = lease?.Id,
            WorkerId = lease?.WorkerId,
            ReasonCode = ReasonCode(job, previousStatus),
            PreviousStatus = previousStatus.ToString(),
            CurrentStatus = job.Status.ToString()
        });
        session.EventsStored++;
        return true;
    }

    private static string ReasonCode(Job job, JobStatus previousStatus) =>
        job.Status == JobStatus.Failed && job.FailureCategory is { } category
            ? $"Failure.{category}"
            : job.Status == JobStatus.Queued && previousStatus != JobStatus.Queued
                && job.RetryReason == "SoftwareDecode"
                ? "Retry.SoftwareDecode"
                : "Job.StatusChanged";
}
