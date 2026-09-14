using Optimisarr.Api.Queue;
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

/// <summary>
/// The dispatcher's choice of *which instant* to measure a worker's head start from. The policy it
/// calls was never wrong; it was being handed the enqueue time, and a library with an optimise
/// window enqueues its work hours before that window opens.
/// </summary>
public sealed class PreferWorkerHoldTests
{
    private static readonly DateTimeOffset WindowOpened =
        new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);

    private static QueuedJob Job(int id, DateTimeOffset enqueuedAt, bool qualityChosen = true) =>
        new(id, LibraryId: 2, Priority: 0, EnqueuedAt: enqueuedAt,
            Placement: WorkPlacement.PreferWorker, QualityChosen: qualityChosen);

    private static Func<QueuedJob, bool> WorkerCouldTake(bool could = true) => _ => could;

    [Fact]
    public void A_backlog_that_waited_all_day_for_its_window_still_offers_the_worker_first_refusal()
    {
        // Enqueued at 07:53, ineligible until midnight: the job spent sixteen hours being offered
        // to nobody. Counting that against the worker handed the whole backlog to this machine the
        // moment the window opened, which is exactly what happened on 2026-09-14.
        var job = Job(5889, WindowOpened.AddHours(-16));
        var firstRunnable = new Dictionary<int, DateTimeOffset> { [5889] = WindowOpened };

        var runnable = QueueDispatcher.SelectLocallyRunnable(
            [job], firstRunnable,
            remoteWorkersEnabled: true, WorkerCouldTake(),
            nowUtc: WindowOpened.AddMinutes(1));

        Assert.Empty(runnable); // held for the worker
    }

    [Fact]
    public void The_hold_still_lapses_so_an_idle_worker_never_strands_the_queue()
    {
        var job = Job(5889, WindowOpened.AddHours(-16));
        var firstRunnable = new Dictionary<int, DateTimeOffset> { [5889] = WindowOpened };

        var runnable = QueueDispatcher.SelectLocallyRunnable(
            [job], firstRunnable,
            remoteWorkersEnabled: true, WorkerCouldTake(),
            nowUtc: WindowOpened.AddMinutes(11));

        Assert.Single(runnable);
    }

    [Fact]
    public void A_job_with_no_recorded_moment_is_treated_as_runnable_now()
    {
        // The map lives in memory, so a restart mid-window leaves nothing recorded. Treating that
        // as "became runnable now" restarts the hold, which errs towards the worker — the safe
        // direction for a setting whose whole purpose is to prefer one.
        var runnable = QueueDispatcher.SelectLocallyRunnable(
            [Job(5889, WindowOpened.AddHours(-16))],
            new Dictionary<int, DateTimeOffset>(),
            remoteWorkersEnabled: true, WorkerCouldTake(),
            nowUtc: WindowOpened);

        Assert.Empty(runnable);
    }

    [Fact]
    public void With_no_worker_able_to_take_it_this_machine_runs_it_at_once()
    {
        var runnable = QueueDispatcher.SelectLocallyRunnable(
            [Job(5889, WindowOpened.AddHours(-16))],
            new Dictionary<int, DateTimeOffset> { [5889] = WindowOpened },
            remoteWorkersEnabled: true, WorkerCouldTake(false),
            nowUtc: WindowOpened);

        Assert.Single(runnable);
    }
}


/// <summary>
/// The interaction that made "prefer a worker" meaningless on every adaptive library: a worker
/// cannot be offered a job until a per-title quality has been chosen, and only the control plane
/// chooses one.
/// </summary>
public sealed class AdaptiveQualityHoldTests
{
    private static readonly DateTimeOffset WindowOpened =
        new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);

    private static QueuedJob Job(bool qualityChosen) =>
        new(5889, LibraryId: 2, Priority: 0, EnqueuedAt: WindowOpened,
            Placement: WorkPlacement.PreferWorker, QualityChosen: qualityChosen);

    [Fact]
    public void A_job_still_awaiting_its_quality_search_is_never_held_for_a_worker()
    {
        // Holding it would wait for something that cannot happen: the claim route refuses a job
        // with no chosen quality, and this machine has promised not to start it. That stalled every
        // adaptive library for the length of the hold and then ran it locally anyway.
        var runnable = QueueDispatcher.SelectLocallyRunnable(
            [Job(qualityChosen: false)],
            new Dictionary<int, DateTimeOffset> { [5889] = WindowOpened },
            remoteWorkersEnabled: true,
            aWorkerCouldTakeIt: job => !QueueDispatcher.AwaitsLocalQualityChoice(job, new HashSet<int> { 2 }),
            nowUtc: WindowOpened);

        Assert.Single(runnable);
    }

    [Fact]
    public void Once_the_quality_is_chosen_the_worker_gets_its_head_start()
    {
        var runnable = QueueDispatcher.SelectLocallyRunnable(
            [Job(qualityChosen: true)],
            new Dictionary<int, DateTimeOffset> { [5889] = WindowOpened },
            remoteWorkersEnabled: true,
            aWorkerCouldTakeIt: job => !QueueDispatcher.AwaitsLocalQualityChoice(job, new HashSet<int> { 2 }),
            nowUtc: WindowOpened);

        Assert.Empty(runnable);
    }

    [Fact]
    public void A_library_that_does_not_choose_per_title_quality_is_unaffected()
    {
        // Nothing to wait for, so the preference applies from the moment the job can run.
        var runnable = QueueDispatcher.SelectLocallyRunnable(
            [Job(qualityChosen: false)],
            new Dictionary<int, DateTimeOffset> { [5889] = WindowOpened },
            remoteWorkersEnabled: true,
            aWorkerCouldTakeIt: job => !QueueDispatcher.AwaitsLocalQualityChoice(job, new HashSet<int>()),
            nowUtc: WindowOpened);

        Assert.Empty(runnable);
    }
}
