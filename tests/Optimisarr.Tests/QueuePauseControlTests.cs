using Microsoft.Extensions.Logging.Abstractions;
using Optimisarr.Api.Queue;
using Optimisarr.Api.Realtime;

namespace Optimisarr.Tests;

public sealed class QueuePauseControlTests
{
    private sealed class RecordingSignals(bool supported = true, Func<int, bool>? suspendOutcome = null)
        : IProcessSignals
    {
        public List<int> Suspended { get; } = [];
        public List<int> Resumed { get; } = [];

        public bool Supported { get; } = supported;

        public bool TrySuspend(int pid)
        {
            Suspended.Add(pid);
            return suspendOutcome?.Invoke(pid) ?? true;
        }

        public bool TryResume(int pid)
        {
            Resumed.Add(pid);
            return true;
        }
    }

    private static QueuePauseControl Control(ActiveEncodeRegistry encodes, RecordingSignals signals) =>
        new(encodes, signals, NullLogger<QueuePauseControl>.Instance);

    [Fact]
    public void Pause_suspends_every_running_encode_and_blocks_dispatch()
    {
        var encodes = new ActiveEncodeRegistry();
        encodes.Track(100, hardwareEncoder: false);
        encodes.Track(200, hardwareEncoder: true);
        var signals = new RecordingSignals();
        var control = Control(encodes, signals);

        control.Pause();

        Assert.True(control.IsPaused);
        Assert.Equal([100, 200], signals.Suspended.Order());
    }

    [Fact]
    public void An_encode_that_starts_while_paused_is_suspended_immediately()
    {
        var encodes = new ActiveEncodeRegistry();
        var signals = new RecordingSignals();
        var control = Control(encodes, signals);
        control.Pause();

        encodes.Track(300, hardwareEncoder: false);
        control.OnEncodeStarted(300);

        Assert.Equal([300], signals.Suspended);
    }

    [Fact]
    public void An_encode_that_starts_while_not_paused_is_left_running()
    {
        var signals = new RecordingSignals();
        var control = Control(new ActiveEncodeRegistry(), signals);

        control.OnEncodeStarted(300);

        Assert.Empty(signals.Suspended);
    }

    [Fact]
    public void Resume_continues_suspended_encodes_and_reopens_dispatch()
    {
        var encodes = new ActiveEncodeRegistry();
        encodes.Track(100, hardwareEncoder: false);
        var signals = new RecordingSignals();
        var control = Control(encodes, signals);
        control.Pause();

        control.Resume();

        Assert.False(control.IsPaused);
        Assert.Equal([100], signals.Resumed);
    }

    [Fact]
    public void Pausing_twice_suspends_each_encode_once()
    {
        var encodes = new ActiveEncodeRegistry();
        encodes.Track(100, hardwareEncoder: false);
        var signals = new RecordingSignals();
        var control = Control(encodes, signals);

        control.Pause();
        control.Pause();

        Assert.Equal([100], signals.Suspended);
    }

    [Fact]
    public void Resuming_while_not_paused_does_nothing()
    {
        var encodes = new ActiveEncodeRegistry();
        encodes.Track(100, hardwareEncoder: false);
        var signals = new RecordingSignals();
        var control = Control(encodes, signals);

        control.Resume();

        Assert.Empty(signals.Resumed);
        Assert.False(control.IsPaused);
    }

    // An encode can exit between the pause click and the signal (the pid is then gone). The
    // failure must not stop the remaining encodes from being suspended.
    [Fact]
    public void A_pid_that_cannot_be_suspended_does_not_stop_the_others()
    {
        var encodes = new ActiveEncodeRegistry();
        encodes.Track(100, hardwareEncoder: false);
        encodes.Track(200, hardwareEncoder: false);
        var signals = new RecordingSignals(suspendOutcome: pid => pid != 100);
        var control = Control(encodes, signals);

        control.Pause();

        Assert.True(control.IsPaused);
        Assert.Equal([100, 200], signals.Suspended.Order());
    }

    // On a platform without POSIX signals the pause still stops new work from starting, but
    // running encodes finish naturally — the reason must say so rather than claim a suspension
    // that never happened.
    [Fact]
    public void Pause_without_signal_support_blocks_dispatch_but_claims_no_suspension()
    {
        var encodes = new ActiveEncodeRegistry();
        encodes.Track(100, hardwareEncoder: false);
        var signals = new RecordingSignals(supported: false);
        var control = Control(encodes, signals);

        control.Pause();

        Assert.True(control.IsPaused);
        Assert.Empty(signals.Suspended);
        Assert.DoesNotContain("suspended", control.BlockedReason);
    }

    [Fact]
    public void Pause_with_signal_support_reports_encodes_as_suspended()
    {
        var control = Control(new ActiveEncodeRegistry(), new RecordingSignals());

        control.Pause();

        Assert.Contains("suspended", control.BlockedReason);
    }
}
