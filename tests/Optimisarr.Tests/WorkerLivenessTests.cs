using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

/// <summary>
/// Liveness decides whether a paired sidecar is currently reachable. It must not flap — a single
/// dropped heartbeat should not mark a healthy worker offline — and it must fail closed, because
/// treating an unreachable worker as online is what would strand a job on a machine that is gone.
/// </summary>
public class WorkerLivenessTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_worker_that_has_never_reported_is_offline()
    {
        Assert.False(WorkerLiveness.IsOnline(null, Now));
    }

    [Fact]
    public void A_worker_that_just_reported_is_online()
    {
        Assert.True(WorkerLiveness.IsOnline(Now, Now));
    }

    [Fact]
    public void A_worker_within_the_offline_threshold_is_still_online()
    {
        var lastSeen = Now - WorkerLiveness.OfflineAfter + TimeSpan.FromSeconds(1);

        Assert.True(WorkerLiveness.IsOnline(lastSeen, Now));
    }

    [Fact]
    public void A_worker_at_or_past_the_threshold_is_offline()
    {
        Assert.False(WorkerLiveness.IsOnline(Now - WorkerLiveness.OfflineAfter, Now));
        Assert.False(WorkerLiveness.IsOnline(Now - TimeSpan.FromHours(3), Now));
    }

    [Fact]
    public void One_missed_heartbeat_does_not_mark_a_worker_offline()
    {
        // The whole point of separating the interval from the threshold. If they were equal, a
        // single late or dropped beat would flap the status.
        var missedOne = Now - WorkerLiveness.HeartbeatInterval - TimeSpan.FromSeconds(1);

        Assert.True(WorkerLiveness.IsOnline(missedOne, Now));
    }

    [Fact]
    public void The_threshold_allows_at_least_two_missed_beats_before_declaring_offline()
    {
        // Pins the relationship rather than the numbers, so tuning either constant cannot
        // accidentally make the status flap on one dropped packet.
        Assert.True(WorkerLiveness.OfflineAfter >= WorkerLiveness.HeartbeatInterval * 3);
    }
}
