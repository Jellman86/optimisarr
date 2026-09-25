using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class ShutdownCountdownTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Idle_worker_counts_down_only_after_a_confirmed_drained_heartbeat()
    {
        var plan = new ShutdownCountdown();
        plan.Arm();
        Assert.False(plan.Evaluate(Now, ready: false, activeJobs: 0));
        Assert.Equal("Waiting for the server to confirm draining", plan.Detail);
        Assert.False(plan.Evaluate(Now, ready: true, activeJobs: 0));
        Assert.Equal(60, plan.SecondsRemaining(Now));
        Assert.True(plan.Evaluate(Now.AddSeconds(60), ready: true, activeJobs: 0));
        Assert.True(plan.Begin());
        Assert.False(plan.Evaluate(Now.AddSeconds(61), ready: true, activeJobs: 0));
    }

    [Fact]
    public void Active_or_failed_job_must_finish_reporting_before_countdown()
    {
        var plan = new ShutdownCountdown();
        plan.Arm();
        Assert.False(plan.Evaluate(Now, ready: false, activeJobs: 1));
        Assert.Equal("Waiting for 1 job to finish verification, return, and acknowledgement", plan.Detail);
        Assert.False(plan.Evaluate(Now.AddSeconds(10), ready: true, activeJobs: 0));
        Assert.Equal(60, plan.SecondsRemaining(Now.AddSeconds(10)));
    }

    [Fact]
    public void Server_disconnect_resets_countdown_and_cancel_prevents_shutdown()
    {
        var plan = new ShutdownCountdown();
        plan.Arm();
        plan.Evaluate(Now, ready: true, activeJobs: 0);
        Assert.False(plan.Evaluate(Now.AddSeconds(30), ready: false, activeJobs: 0, serverUnreachable: true));
        Assert.Contains("Server unreachable", plan.Detail);
        Assert.False(plan.Evaluate(Now.AddSeconds(61), ready: true, activeJobs: 0));
        Assert.Equal(60, plan.SecondsRemaining(Now.AddSeconds(61)));
        plan.Cancel();
        Assert.False(plan.Evaluate(Now.AddSeconds(200), ready: true, activeJobs: 0));
    }

    [Fact]
    public void Permission_failure_is_visible_and_never_retried_automatically()
    {
        var plan = new ShutdownCountdown();
        plan.Arm();
        plan.Evaluate(Now, ready: true, activeJobs: 0);
        Assert.True(plan.Evaluate(Now.AddSeconds(60), ready: true, activeJobs: 0));
        Assert.True(plan.Begin());
        plan.Fail("Windows denied shutdown permission");
        Assert.Equal("Windows denied shutdown permission", plan.Detail);
        Assert.False(plan.Evaluate(Now.AddSeconds(120), ready: true, activeJobs: 0));
    }

    [Fact]
    public void Unacknowledged_result_blocks_shutdown_even_when_no_job_is_running()
    {
        var plan = new ShutdownCountdown();
        plan.Arm();
        Assert.False(plan.Evaluate(Now, ready: false, activeJobs: 0, unacknowledged: true));
        Assert.Contains("not acknowledged", plan.Detail);
        Assert.Null(plan.SecondsRemaining(Now));
    }

    [Fact]
    public void Cancel_during_final_server_check_still_prevents_power_off()
    {
        var plan = new ShutdownCountdown();
        plan.Arm();
        plan.Evaluate(Now, ready: true, activeJobs: 0);
        Assert.True(plan.Evaluate(Now.AddSeconds(60), ready: true, activeJobs: 0));
        Assert.True(plan.CanCancel);
        plan.Cancel();
        Assert.False(plan.Begin());
    }
}
