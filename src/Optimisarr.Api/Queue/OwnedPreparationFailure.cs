using Optimisarr.Data;
using Optimisarr.Api.Replacement;
using Optimisarr.Api.Deduplication;

namespace Optimisarr.Api.Queue;

/// <summary>An interrupted attempt is actionable state, never evidence of damaged media.</summary>
internal static class OwnedPreparationFailure
{
    public static bool IsDeferral(Exception error) => error is FileCleanupDeferredException or DedupeReservationConflictException or AttemptContentChangedException;

    public static bool TryApply(Job job, int attempt, string reason, DateTimeOffset now)
    {
        if (job.ExecutionAttempt != attempt || job.Status is not (JobStatus.Probing or JobStatus.Transcoding or JobStatus.Verifying)) return false;
        job.Status = JobStatus.Failed;
        job.ErrorMessage = reason;
        job.FinishedAt = now;
        job.UpdatedAt = now;
        return true;
    }
}
