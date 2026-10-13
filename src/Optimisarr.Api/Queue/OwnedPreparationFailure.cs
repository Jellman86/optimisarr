using Optimisarr.Data;
using Optimisarr.Api.Deduplication;

namespace Optimisarr.Api.Queue;

/// <summary>A reservation/ownership refusal is actionable state, never evidence of damaged media.</summary>
internal static class OwnedPreparationFailure
{
    public static bool IsDeferral(Exception error) => error is FileCleanupDeferredException or DedupeReservationConflictException;

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
