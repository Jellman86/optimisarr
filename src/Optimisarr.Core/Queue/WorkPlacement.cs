namespace Optimisarr.Core.Queue;

/// <summary>
/// Where a library's video re-encodes may run once remote workers are switched on. The choice is a
/// filter over one shared queue, never a second queue: a job keeps its priority and age wherever it
/// is allowed to run. While remote workers are off, every value behaves as <see cref="Anywhere"/>,
/// because there is nowhere else for the work to go and a library must not stall for a feature that
/// is not in use.
/// </summary>
public enum WorkPlacement
{
    /// <summary>Whichever machine is free first takes the job.</summary>
    Anywhere = 0,

    /// <summary>Never offered to a worker; only this server encodes it.</summary>
    LocalOnly = 1,

    /// <summary>
    /// Held for an online worker that could run it, for a bounded time; this server takes it after
    /// <see cref="WorkPlacementPolicy.PreferWorkerHold"/> if no worker has.
    /// </summary>
    PreferWorker = 2,

    /// <summary>Never encoded here; waits until a worker takes it.</summary>
    WorkerOnly = 3
}

/// <summary>
/// The two questions each side of the queue asks about a job's placement. Pure so the rule is unit
/// tested once rather than re-derived in the dispatcher and the claim endpoint.
/// </summary>
public static class WorkPlacementPolicy
{
    /// <summary>
    /// How long a <see cref="WorkPlacement.PreferWorker"/> job waits for a worker before this server
    /// takes it. Long enough for a sleeping laptop to wake and poll, short enough that a queue does
    /// not stall visibly when the only worker is busy.
    /// </summary>
    public static readonly TimeSpan PreferWorkerHold = TimeSpan.FromMinutes(10);

    /// <summary>Whether a job with this placement may be offered to a remote worker at all.</summary>
    public static bool MayRunOnWorker(WorkPlacement placement) => placement != WorkPlacement.LocalOnly;

    /// <summary>
    /// Whether this server may start the job now. <paramref name="remoteWorkersEnabled"/> is the
    /// global switch; while it is off no placement can hold work back. <paramref name="aWorkerCouldTakeIt"/>
    /// says an online, non-draining worker exists, which is what a preference can reasonably wait for.
    /// </summary>
    public static bool MayRunLocally(
        WorkPlacement placement,
        bool remoteWorkersEnabled,
        bool aWorkerCouldTakeIt,
        DateTimeOffset enqueuedAt,
        DateTimeOffset now)
    {
        if (!remoteWorkersEnabled)
        {
            return true;
        }

        return placement switch
        {
            WorkPlacement.WorkerOnly => false,
            WorkPlacement.PreferWorker => !aWorkerCouldTakeIt || now - enqueuedAt >= PreferWorkerHold,
            _ => true,
        };
    }
}
