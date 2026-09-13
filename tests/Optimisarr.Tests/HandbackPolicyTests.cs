using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

/// <summary>
/// A released job returns to the queue and the claim loop offers the highest-priority queued job
/// to whoever asks next. Without a memory of who gave it back, a worker that cannot run a job is
/// handed it again every check-in, downloading the source afresh each time.
/// </summary>
public class HandbackPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_job_no_one_has_refused_may_be_offered()
    {
        Assert.True(HandbackPolicy.MayOffer(lastHandbackByThisWorker: null, handbacksByAnyone: 0, Now));
    }

    [Fact]
    public void A_worker_is_not_offered_a_job_it_just_handed_back()
    {
        Assert.False(HandbackPolicy.MayOffer(Now.AddMinutes(-1), handbacksByAnyone: 1, Now));
    }

    [Fact]
    public void The_cooldown_expires_so_a_worker_that_was_merely_asleep_is_useful_again()
    {
        // The common handback is a laptop sleeping mid-job, not a job it can never run. Excluding
        // that Mac from this one file forever would be the wrong lesson to draw.
        Assert.True(HandbackPolicy.MayOffer(Now - HandbackPolicy.Cooldown, handbacksByAnyone: 1, Now));
    }

    [Fact]
    public void Another_worker_is_still_offered_a_job_during_someone_elses_cooldown()
    {
        // The pause is per worker: a job one machine cannot encode may be exactly what another can.
        Assert.True(HandbackPolicy.MayOffer(lastHandbackByThisWorker: null, handbacksByAnyone: 1, Now));
    }

    [Fact]
    public void A_job_handed_back_repeatedly_stops_being_offered_to_workers_at_all()
    {
        // Three separate refusals is not bad luck. It stays queued for this server to run, and the
        // worker cards say what went wrong.
        Assert.False(HandbackPolicy.MayOffer(lastHandbackByThisWorker: null, HandbackPolicy.MaxHandbacks, Now));
    }

    [Fact]
    public void The_reason_says_which_rule_applied()
    {
        Assert.Contains("no longer offered", HandbackPolicy.Explain(null, HandbackPolicy.MaxHandbacks, Now));
        Assert.Contains("handed it back", HandbackPolicy.Explain(Now.AddMinutes(-2), 1, Now));
    }
}
