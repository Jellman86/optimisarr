using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

/// <summary>
/// A job seen frozen at 100% with "~2s left" until the container was restarted (#95). The status
/// was still Transcoding, which means ffmpeg had not exited; on a healthy encode the final
/// progress block and the exit are the same instant. The monitor turns that silence into a
/// failed job with a reason, from timestamps alone so it needs no process to test.
/// </summary>
public sealed class EncodeStallMonitorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_healthy_encode_is_never_stalled()
    {
        var monitor = new EncodeStallMonitor(T0);

        for (var minute = 1; minute <= 120; minute++)
        {
            monitor.Touch(T0.AddMinutes(minute));
            Assert.Null(monitor.Check(T0.AddMinutes(minute).AddSeconds(15)));
        }
    }

    [Fact]
    public void Silence_longer_than_the_limit_is_a_stall()
    {
        var monitor = new EncodeStallMonitor(T0);
        monitor.Touch(T0.AddMinutes(10));

        Assert.Null(monitor.Check(T0.AddMinutes(39)));
        Assert.Equal(EncodeStallKind.NoProgressReported, monitor.Check(T0.AddMinutes(40)));
    }

    [Fact]
    public void Silence_before_the_first_line_counts_from_the_start()
    {
        // A hardware encoder that never initialises writes nothing at all.
        var monitor = new EncodeStallMonitor(T0);

        Assert.Equal(EncodeStallKind.NoProgressReported, monitor.Check(T0.AddMinutes(30)));
    }

    [Fact]
    public void Not_exiting_after_the_final_block_is_a_stall_on_its_own_shorter_clock()
    {
        var monitor = new EncodeStallMonitor(T0);
        monitor.Touch(T0.AddMinutes(5));
        monitor.FinalReported(T0.AddMinutes(6));

        Assert.Null(monitor.Check(T0.AddMinutes(7).AddSeconds(59)));
        Assert.Equal(
            EncodeStallKind.DidNotExitAfterFinalReport,
            monitor.Check(T0.AddMinutes(8)));
    }

    [Fact]
    public void The_final_report_time_is_the_first_one_seen()
    {
        var monitor = new EncodeStallMonitor(T0);
        monitor.FinalReported(T0.AddMinutes(1));
        monitor.FinalReported(T0.AddMinutes(1).AddSeconds(30));

        Assert.Equal(T0.AddMinutes(1), monitor.FinalReportAt);
    }

    [Fact]
    public void A_paused_encode_is_never_stalled_and_gets_a_full_window_back_on_resume()
    {
        // SIGSTOP silences ffmpeg on purpose; that silence proves nothing.
        var monitor = new EncodeStallMonitor(T0);
        monitor.Touch(T0.AddMinutes(1));

        Assert.Null(monitor.Check(T0.AddHours(3), paused: true));
        Assert.Null(monitor.Check(T0.AddHours(3).AddMinutes(29)));
        Assert.Equal(EncodeStallKind.NoProgressReported, monitor.Check(T0.AddHours(3).AddMinutes(30)));
    }

    [Fact]
    public void The_limits_are_configurable_for_callers_that_know_better()
    {
        var monitor = new EncodeStallMonitor(
            T0,
            silenceLimit: TimeSpan.FromSeconds(10),
            exitGraceAfterFinalReport: TimeSpan.FromSeconds(1));

        Assert.Equal(EncodeStallKind.NoProgressReported, monitor.Check(T0.AddSeconds(10)));
        monitor.FinalReported(T0.AddSeconds(10));
        Assert.Equal(EncodeStallKind.DidNotExitAfterFinalReport, monitor.Check(T0.AddSeconds(11)));
    }

    [Fact]
    public void The_reason_names_what_was_observed_and_for_how_long()
    {
        var monitor = new EncodeStallMonitor(T0);

        var afterFinal = monitor.Describe(EncodeStallKind.DidNotExitAfterFinalReport);
        var silent = monitor.Describe(EncodeStallKind.NoProgressReported);

        Assert.Contains("reported the encode complete", afterFinal);
        Assert.Contains("2 minutes", afterFinal);
        Assert.Contains("no progress for 30 minutes", silent);
        Assert.Contains("not paused", silent);
    }
}
