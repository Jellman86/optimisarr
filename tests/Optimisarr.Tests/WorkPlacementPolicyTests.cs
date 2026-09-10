using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

/// <summary>
/// The placement rule is one shared queue with a filter on each side, never a second queue. These
/// tests pin the two questions each side asks, and the one case where the whole rule steps aside:
/// remote workers switched off, when holding work for them would only stall the library.
/// </summary>
public sealed class WorkPlacementPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(WorkPlacement.Anywhere, true)]
    [InlineData(WorkPlacement.PreferWorker, true)]
    [InlineData(WorkPlacement.WorkerOnly, true)]
    [InlineData(WorkPlacement.LocalOnly, false)]
    public void Only_a_local_only_library_is_kept_away_from_workers(WorkPlacement placement, bool offered)
    {
        Assert.Equal(offered, WorkPlacementPolicy.MayRunOnWorker(placement));
    }

    [Theory]
    [InlineData(WorkPlacement.Anywhere)]
    [InlineData(WorkPlacement.LocalOnly)]
    [InlineData(WorkPlacement.PreferWorker)]
    [InlineData(WorkPlacement.WorkerOnly)]
    public void While_remote_workers_are_off_every_placement_runs_here(WorkPlacement placement)
    {
        // There is nowhere else for the work to go. A library that says "only on workers" must not
        // sit for ever because the feature it named is not in use.
        Assert.True(WorkPlacementPolicy.MayRunLocally(
            placement, remoteWorkersEnabled: false, aWorkerCouldTakeIt: true, Now, Now));
    }

    [Fact]
    public void A_worker_only_job_never_starts_here_while_workers_are_on()
    {
        Assert.False(WorkPlacementPolicy.MayRunLocally(
            WorkPlacement.WorkerOnly, remoteWorkersEnabled: true, aWorkerCouldTakeIt: false,
            enqueuedAt: Now.AddDays(-1), now: Now));
    }

    [Fact]
    public void A_preferred_worker_job_is_held_while_a_worker_could_take_it()
    {
        Assert.False(WorkPlacementPolicy.MayRunLocally(
            WorkPlacement.PreferWorker, remoteWorkersEnabled: true, aWorkerCouldTakeIt: true,
            enqueuedAt: Now.AddMinutes(-2), now: Now));
    }

    [Fact]
    public void A_preferred_worker_job_starts_here_once_the_hold_has_run_out()
    {
        // The worker existed but never claimed — busy, or asleep longer than it should be. The
        // preference was for a worker, not against this server, so the job goes on.
        Assert.True(WorkPlacementPolicy.MayRunLocally(
            WorkPlacement.PreferWorker, remoteWorkersEnabled: true, aWorkerCouldTakeIt: true,
            enqueuedAt: Now - WorkPlacementPolicy.PreferWorkerHold, now: Now));
    }

    [Fact]
    public void A_preferred_worker_job_starts_here_at_once_when_no_worker_could_take_it()
    {
        // Nothing to wait for: every worker offline, draining or revoked.
        Assert.True(WorkPlacementPolicy.MayRunLocally(
            WorkPlacement.PreferWorker, remoteWorkersEnabled: true, aWorkerCouldTakeIt: false,
            enqueuedAt: Now, now: Now));
    }

    [Theory]
    [InlineData(WorkPlacement.Anywhere)]
    [InlineData(WorkPlacement.LocalOnly)]
    public void Anywhere_and_local_only_jobs_are_never_held(WorkPlacement placement)
    {
        Assert.True(WorkPlacementPolicy.MayRunLocally(
            placement, remoteWorkersEnabled: true, aWorkerCouldTakeIt: true, Now, Now));
    }
}
