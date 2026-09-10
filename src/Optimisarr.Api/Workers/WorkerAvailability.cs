using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Api.Workers;

/// <summary>
/// The two facts a placement decision needs about the worker fleet, resolved once per question
/// so the dispatcher and the queue feed cannot disagree about whether a job is waiting for a
/// worker. Remote work is on only when the switch is on <em>and</em> the preview flag is present,
/// because without the flag every worker route refuses and nothing could ever claim.
/// </summary>
public sealed record WorkerAvailability(bool RemoteWorkersOn, bool AWorkerCouldTakeWork)
{
    public static readonly WorkerAvailability Off = new(false, false);

    /// <summary>
    /// A worker "could take work" when it is paired, not revoked, not draining, reports a
    /// concurrency above zero, and has checked in inside the offline threshold.
    /// </summary>
    public static async Task<WorkerAvailability> ResolveAsync(
        OptimisarrDbContext db,
        bool remoteWorkersEnabled,
        RemoteWorkersFeature feature,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (!remoteWorkersEnabled || !feature.Available)
        {
            return Off;
        }

        // The liveness comparison runs in memory: SQLite cannot compare a DateTimeOffset column,
        // the same constraint every date filter in the queue works around. Paired workers are
        // few, so the candidate list is cheap to pull.
        var lastSeen = await db.Workers
            .AsNoTracking()
            .Where(worker => worker.RevokedAt == null
                && worker.DrainRequestedAt == null
                && worker.MaxConcurrency > 0
                && worker.LastSeenAt != null)
            .Select(worker => worker.LastSeenAt)
            .ToListAsync(cancellationToken);
        return new WorkerAvailability(true, lastSeen.Any(seen => WorkerLiveness.IsOnline(seen, nowUtc)));
    }
}
