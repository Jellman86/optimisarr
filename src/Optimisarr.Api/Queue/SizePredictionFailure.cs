using Optimisarr.Core.Queue;
using Optimisarr.Data;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Workers;

namespace Optimisarr.Api.Queue;

internal static class SizePredictionFailure
{
    public static async Task<bool> RejectWorkerAsync(OptimisarrDbContext db, JobLease lease,
        int workerId, int quality, string reason, string probesJson, CancellationToken cancellationToken)
    {
        // Forecasting can outlive a cancellation or lease expiry. Refresh under the same
        // database transaction boundary used by cancellation before committing a verdict.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Entry(lease).ReloadAsync(cancellationToken);
        if (lease.Job is not { } job || db.Entry(lease).State == EntityState.Detached) return false;
        await db.Entry(job).ReloadAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (db.Entry(job).State == EntityState.Detached || job.Status != JobStatus.Leased) return false;
        if (lease.ToDomain().StateAt(now) != LeaseState.Held) return false;
        var release = lease.ToDomain().Release(workerId, now);
        if (release.Outcome != LeaseOutcome.Released) return false;
        lease.AdaptiveProbesJson = probesJson;
        lease.AdaptiveAskedQuality = null;
        lease.Apply(release.Lease, now);
        lease.EndReason = LeaseEndReason.PredictedSizeFailure;
        Apply(job, quality, reason, now);
        await QueueDispatcher.ApplyFailureTrackingAsync(db, job, JobStatus.Failed);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public static void Apply(Job job, int? quality, string? reason, DateTimeOffset now)
    {
        job.Status = JobStatus.Failed;
        job.AdaptiveVideoQuality = quality;
        job.Progress = 0;
        var explanation = reason?.Replace("The full encode is held until you choose to run it.", "The full encode was not run.")
            .Replace("and the final size and quality checks still apply.", "and no replacement was made.");
        job.ErrorMessage = explanation?.StartsWith("Size saving prediction:", StringComparison.Ordinal) == true
            ? explanation : "Size saving prediction: " + (explanation ?? "Samples predicted an output above the configured size limit. The full encode was not run; the original is unchanged.");
        job.FailureCategory = FailureCategory.SizeSaving;
        job.FinishedAt = now;
        job.UpdatedAt = now;
    }
}
