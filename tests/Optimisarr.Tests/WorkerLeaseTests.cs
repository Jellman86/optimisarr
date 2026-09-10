using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

/// <summary>
/// A lease is what stops two machines encoding the same job. The dangerous failure is not losing a
/// lease — that just wastes work — but releasing one while its holder is still running, because
/// then two workers race to produce a candidate for the same original. These tests pin the
/// behaviour that prevents it.
/// </summary>
public class WorkerLeaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid LeaseId = new("11111111-1111-1111-1111-111111111111");

    private static WorkerLease Held() => WorkerLease.Acquire(LeaseId, jobId: 42, workerId: 7, Now);

    [Fact]
    public void A_lease_can_only_expire_after_its_holder_would_be_declared_offline()
    {
        // The invariant the whole design rests on. If a lease could lapse while its holder still
        // counted as online, the job could be handed to a second worker while the first was still
        // encoding it. Asserted as a relationship so tuning either constant cannot break it
        // silently.
        Assert.True(WorkerLease.Duration > WorkerLiveness.OfflineAfter);
    }

    [Fact]
    public void Acquire_holds_the_job_for_the_lease_duration()
    {
        var lease = Held();

        Assert.Equal(LeaseState.Held, lease.StateAt(Now));
        Assert.Equal(Now + WorkerLease.Duration, lease.ExpiresUtc);
        Assert.Equal(42, lease.JobId);
        Assert.Equal(7, lease.WorkerId);
    }

    [Fact]
    public void A_held_lease_past_its_expiry_reads_as_expired_without_anyone_sweeping_it()
    {
        // Correctness must not depend on a background job running. A sweeper may tidy rows up, but
        // a lapsed lease has to be lapsed the moment it is read, or a crashed control plane would
        // wake up still believing a dead worker holds the job.
        var lease = Held();

        Assert.Equal(LeaseState.Held, lease.StateAt(lease.ExpiresUtc - TimeSpan.FromSeconds(1)));
        Assert.Equal(LeaseState.Expired, lease.StateAt(lease.ExpiresUtc));
        Assert.Equal(LeaseState.Expired, lease.StateAt(lease.ExpiresUtc + TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Renew_extends_the_expiry_from_the_moment_of_renewal()
    {
        var lease = Held();
        var later = Now + TimeSpan.FromMinutes(1);

        var result = lease.Renew(byWorkerId: 7, later);

        Assert.Equal(LeaseOutcome.Renewed, result.Outcome);
        Assert.Equal(later + WorkerLease.Duration, result.Lease.ExpiresUtc);
    }

    [Fact]
    public void Renew_refuses_a_lease_that_has_already_lapsed()
    {
        // Resurrecting a lapsed lease is the exact path to two holders: the job may already have
        // been offered to someone else, so a late renewal must fail rather than reclaim it.
        var lease = Held();

        var result = lease.Renew(byWorkerId: 7, lease.ExpiresUtc);

        Assert.Equal(LeaseOutcome.Expired, result.Outcome);
        Assert.Equal(LeaseState.Expired, result.Lease.StateAt(lease.ExpiresUtc));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Another_worker_cannot_renew_release_or_complete_someone_elses_lease(int intruder)
    {
        var lease = Held();

        Assert.Equal(LeaseOutcome.NotHolder, lease.Renew(intruder, Now).Outcome);
        Assert.Equal(LeaseOutcome.NotHolder, lease.Release(intruder, Now).Outcome);
        Assert.Equal(LeaseOutcome.NotHolder, lease.Complete(intruder, Now).Outcome);
    }

    [Fact]
    public void A_rejected_operation_leaves_the_lease_untouched()
    {
        var lease = Held();

        var result = lease.Release(byWorkerId: 8, Now);

        Assert.Equal(LeaseOutcome.NotHolder, result.Outcome);
        Assert.Equal(LeaseState.Held, result.Lease.StateAt(Now));
        Assert.Equal(lease.ExpiresUtc, result.Lease.ExpiresUtc);
    }

    [Fact]
    public void Release_hands_the_job_back()
    {
        var result = Held().Release(byWorkerId: 7, Now);

        Assert.Equal(LeaseOutcome.Released, result.Outcome);
        Assert.Equal(LeaseState.Released, result.Lease.StateAt(Now));
    }

    [Fact]
    public void Release_is_idempotent_so_a_retrying_worker_is_not_punished()
    {
        var once = Held().Release(byWorkerId: 7, Now);

        var twice = once.Lease.Release(byWorkerId: 7, Now + TimeSpan.FromSeconds(5));

        Assert.Equal(LeaseOutcome.Released, twice.Outcome);
        Assert.Equal(LeaseState.Released, twice.Lease.StateAt(Now));
    }

    [Fact]
    public void Complete_marks_the_lease_finished()
    {
        var result = Held().Complete(byWorkerId: 7, Now + TimeSpan.FromMinutes(1));

        Assert.Equal(LeaseOutcome.Completed, result.Outcome);
        Assert.Equal(LeaseState.Completed, result.Lease.StateAt(Now + TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void A_completed_lease_never_reverts_to_expired_once_its_window_passes()
    {
        // A finished result must not start reading as a lapsed lease simply because time moved on,
        // or a delivered candidate could look abandoned and be re-dispatched.
        var completed = Held().Complete(byWorkerId: 7, Now).Lease;

        Assert.Equal(LeaseState.Completed, completed.StateAt(Now + WorkerLease.Duration * 10));
    }

    [Fact]
    public void A_lapsed_lease_cannot_be_completed_afterwards()
    {
        // The late-result case: the worker went away, the lease lapsed, and it then comes back with
        // a candidate. That result must not be accepted through this lease, because the job may
        // already be elsewhere.
        var lease = Held();

        var result = lease.Complete(byWorkerId: 7, lease.ExpiresUtc + TimeSpan.FromSeconds(1));

        Assert.Equal(LeaseOutcome.Expired, result.Outcome);
    }

    [Fact]
    public void A_released_lease_cannot_be_completed()
    {
        var released = Held().Release(byWorkerId: 7, Now).Lease;

        Assert.Equal(LeaseOutcome.NotHeld, released.Complete(byWorkerId: 7, Now).Outcome);
    }

    [Fact]
    public void A_released_lease_cannot_be_renewed_back_into_life()
    {
        var released = Held().Release(byWorkerId: 7, Now).Lease;

        Assert.Equal(LeaseOutcome.NotHeld, released.Renew(byWorkerId: 7, Now).Outcome);
    }

    [Fact]
    public void Renewal_at_the_heartbeat_interval_keeps_a_live_worker_comfortably_ahead()
    {
        // A worker beating normally should never be at risk of losing its lease. Walk several
        // intervals and confirm it stays held throughout.
        var lease = Held();
        var clock = Now;

        for (var i = 0; i < 20; i++)
        {
            clock += WorkerLiveness.HeartbeatInterval;
            var result = lease.Renew(byWorkerId: 7, clock);
            Assert.Equal(LeaseOutcome.Renewed, result.Outcome);
            lease = result.Lease;
        }

        Assert.Equal(LeaseState.Held, lease.StateAt(clock));
    }
}
