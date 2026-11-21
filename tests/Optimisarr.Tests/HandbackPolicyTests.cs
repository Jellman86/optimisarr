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
        Assert.True(HandbackPolicy.MayOffer(lastHandbackByThisWorker: null, workersWhoRefused: 0, Now));
    }

    [Fact]
    public void A_worker_is_not_offered_a_job_it_just_handed_back()
    {
        Assert.False(HandbackPolicy.MayOffer(Now.AddMinutes(-1), workersWhoRefused: 1, Now));
    }

    [Fact]
    public void The_cooldown_expires_so_a_worker_that_was_merely_asleep_is_useful_again()
    {
        // The common handback is a laptop sleeping mid-job, not a job it can never run. Excluding
        // that Mac from this one file forever would be the wrong lesson to draw.
        Assert.True(HandbackPolicy.MayOffer(Now - HandbackPolicy.Cooldown, workersWhoRefused: 1, Now));
    }

    [Fact]
    public void Another_worker_is_still_offered_a_job_during_someone_elses_cooldown()
    {
        // The pause is per worker: a job one machine cannot encode may be exactly what another can.
        Assert.True(HandbackPolicy.MayOffer(lastHandbackByThisWorker: null, workersWhoRefused: 1, Now));
    }

    [Fact]
    public void A_job_three_different_workers_refused_stops_being_offered_to_any()
    {
        // Three separate machines refusing is not bad luck. It stays queued for this server to run,
        // and the worker cards say what went wrong.
        Assert.False(HandbackPolicy.MayOffer(
            lastHandbackByThisWorker: null, HandbackPolicy.MaxRefusingWorkers, Now));
    }

    [Fact]
    public void One_broken_worker_cannot_bar_a_job_from_the_rest_of_the_fleet()
    {
        // What happened on 2026-09-15: a sidecar that could not read its assignment claimed and
        // returned everything it was offered, three times each, and the head of the queue stopped
        // being offered to a healthy machine sitting beside it. The count comes from released
        // leases and never decays, so nothing undid it.
        //
        // One machine's repeated refusals are one machine's opinion. A second worker, which has
        // refused nothing, is still offered the job.
        Assert.True(HandbackPolicy.MayOffer(lastHandbackByThisWorker: null, workersWhoRefused: 1, Now));
    }

    [Fact]
    public void The_worker_that_refused_it_still_waits_out_its_own_cooldown()
    {
        // Counting machines rather than refusals must not turn into "retry immediately, for ever":
        // the machine that refused is still left alone, it just no longer speaks for the others.
        Assert.False(HandbackPolicy.MayOffer(Now.AddMinutes(-2), workersWhoRefused: 1, Now));
        Assert.True(HandbackPolicy.MayOffer(Now - HandbackPolicy.Cooldown, workersWhoRefused: 1, Now));
    }

    [Fact]
    public void The_reason_says_which_rule_applied()
    {
        Assert.Contains(
            "no longer offered", HandbackPolicy.Explain(null, HandbackPolicy.MaxRefusingWorkers, Now));
        Assert.Contains(
            "different workers", HandbackPolicy.Explain(null, HandbackPolicy.MaxRefusingWorkers, Now));
        Assert.Contains("handed it back", HandbackPolicy.Explain(Now.AddMinutes(-2), 1, Now));
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(4, 80)]
    [InlineData(6, 320)]
    [InlineData(7, 360)]
    [InlineData(40, 360)]
    public void Each_refusal_from_the_same_worker_doubles_its_wait_up_to_six_hours(int refusals, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), HandbackPolicy.CooldownAfter(refusals));
        Assert.False(HandbackPolicy.MayOffer(Now.AddMinutes(-minutes).AddSeconds(1), 1, Now, refusals));
        Assert.True(HandbackPolicy.MayOffer(Now.AddMinutes(-minutes), 1, Now, refusals));
    }

    [Fact]
    public void A_worker_that_fails_a_job_the_same_way_all_night_tries_a_handful_of_times_not_twenty()
    {
        // 2026-11-09: Boothby failed two Pokémon episodes at the same frame every 13 minutes from
        // 02:03 to 08:09, 20 times each, downloading 800 MB of source each time. A fixed ten-minute
        // pause allowed that. Doubling it leaves room for a sleeping laptop but not for that.
        var start = Now;
        var handedBack = new List<DateTimeOffset> { start };
        for (var minute = 1; minute <= 6 * 60; minute++)
        {
            var now = start.AddMinutes(minute);
            if (HandbackPolicy.MayOffer(handedBack[^1], 1, now, handedBack.Count))
            {
                handedBack.Add(now.AddMinutes(3));
            }
        }

        Assert.InRange(handedBack.Count, 4, 6);
    }

    [Fact]
    public void The_reason_gives_the_longer_wait_after_repeated_refusals()
    {
        Assert.Contains("hours", HandbackPolicy.Explain(Now.AddMinutes(-5), 1, Now, refusalsByThisWorker: 6));
    }
}
