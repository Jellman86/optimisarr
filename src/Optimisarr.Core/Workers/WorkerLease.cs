namespace Optimisarr.Core.Workers;

/// <summary>Where a lease stands. <see cref="Expired"/> is derived from the clock, never stored.</summary>
public enum LeaseState
{
    Held,
    Released,
    Expired,
    Completed
}

/// <summary>Why a lease operation was accepted or refused.</summary>
public enum LeaseOutcome
{
    Renewed,
    Released,
    Completed,

    /// <summary>Another worker tried to touch a lease it does not hold.</summary>
    NotHolder,

    /// <summary>The lease lapsed; the job may already belong to someone else.</summary>
    Expired,

    /// <summary>The lease was already released or completed, so there is nothing to act on.</summary>
    NotHeld
}

/// <summary>The lease's new state alongside the outcome. A refused operation returns it unchanged.</summary>
public sealed record LeaseResult(LeaseOutcome Outcome, WorkerLease Lease);

/// <summary>
/// One worker's exclusive claim on one job, with an expiry.
///
/// This is what stops two machines encoding the same original. The dangerous failure is not losing
/// a lease — that merely wastes work — but freeing one while its holder is still running, because
/// two candidates would then race for the same source. <see cref="Duration"/> is therefore longer
/// than <see cref="WorkerLiveness.OfflineAfter"/>: a job can only be reclaimed after its holder has
/// already been declared unreachable, never while it still counts as online.
/// </summary>
public sealed record WorkerLease(
    Guid Id,
    int JobId,
    int WorkerId,
    DateTimeOffset AcquiredUtc,
    DateTimeOffset ExpiresUtc,
    LeaseState State)
{
    /// <summary>
    /// How long a claim survives without renewal. Comfortably beyond the point a silent worker is
    /// declared offline, so a reclaim never overlaps a holder still believed to be alive.
    /// </summary>
    public static readonly TimeSpan Duration = WorkerLiveness.OfflineAfter + TimeSpan.FromMinutes(3);

    public static WorkerLease Acquire(Guid id, int jobId, int workerId, DateTimeOffset nowUtc) =>
        new(id, jobId, workerId, nowUtc, nowUtc + Duration, LeaseState.Held);

    /// <summary>
    /// The lease's effective state at <paramref name="nowUtc"/>. Expiry is computed rather than
    /// stored, so a lapsed lease is lapsed the moment it is read. Correctness cannot depend on a
    /// sweeper having run — otherwise a control plane restarting after downtime would wake up
    /// still believing a long-dead worker holds the job.
    /// </summary>
    public LeaseState StateAt(DateTimeOffset nowUtc) =>
        State == LeaseState.Held && nowUtc >= ExpiresUtc ? LeaseState.Expired : State;

    /// <summary>Extends the claim from the moment of renewal, if the caller still holds it.</summary>
    public LeaseResult Renew(int byWorkerId, DateTimeOffset nowUtc) =>
        Guard(byWorkerId, nowUtc)
            ?? new LeaseResult(LeaseOutcome.Renewed, this with { ExpiresUtc = nowUtc + Duration });

    /// <summary>
    /// Hands the job back. Idempotent: releasing an already-released lease succeeds rather than
    /// erroring, so a worker retrying after a dropped response is not punished for being careful.
    /// </summary>
    public LeaseResult Release(int byWorkerId, DateTimeOffset nowUtc)
    {
        if (byWorkerId != WorkerId)
        {
            return new LeaseResult(LeaseOutcome.NotHolder, this);
        }

        if (StateAt(nowUtc) == LeaseState.Released)
        {
            return new LeaseResult(LeaseOutcome.Released, this);
        }

        return Guard(byWorkerId, nowUtc)
            ?? new LeaseResult(LeaseOutcome.Released, this with { State = LeaseState.Released });
    }

    /// <summary>
    /// Marks the claim finished because a result was delivered. A lapsed lease cannot be completed:
    /// that is the late-result case, where a worker vanished, the job moved on, and it then returns
    /// with a candidate that must not be accepted through a claim it no longer holds.
    /// </summary>
    public LeaseResult Complete(int byWorkerId, DateTimeOffset nowUtc) =>
        Guard(byWorkerId, nowUtc)
            ?? new LeaseResult(LeaseOutcome.Completed, this with { State = LeaseState.Completed });

    /// <summary>
    /// The checks every operation shares, in the order that matters: ownership first, so a stranger
    /// learns nothing about a lease's state; then whether the claim is still live.
    /// </summary>
    private LeaseResult? Guard(int byWorkerId, DateTimeOffset nowUtc)
    {
        if (byWorkerId != WorkerId)
        {
            return new LeaseResult(LeaseOutcome.NotHolder, this);
        }

        return StateAt(nowUtc) switch
        {
            LeaseState.Held => null,
            LeaseState.Expired => new LeaseResult(LeaseOutcome.Expired, this),
            _ => new LeaseResult(LeaseOutcome.NotHeld, this)
        };
    }
}
