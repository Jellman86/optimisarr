using Microsoft.Extensions.Logging.Abstractions;
using Optimisarr.Api.Queue;
using Optimisarr.Api.Realtime;

namespace Optimisarr.Tests;

public sealed class QueuePauseControlTests
{
    private sealed class RecordingSignals(
        bool supported = true,
        Func<int, bool>? suspendOutcome = null,
        Func<int, bool>? resumeOutcome = null)
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
            return resumeOutcome?.Invoke(pid) ?? true;
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
    public void A_reused_pid_is_signalled_for_the_new_encode_while_paused()
    {
        var encodes = new ActiveEncodeRegistry();
        var oldRegistration = encodes.Track(300, hardwareEncoder: false);
        var signals = new RecordingSignals();
        var control = Control(encodes, signals);
        control.Pause();
        oldRegistration.Dispose();

        using var newRegistration = encodes.Track(300, hardwareEncoder: false);
        control.OnEncodeStarted(300);

        Assert.Equal([300, 300], signals.Suspended);
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

        var result = control.Resume();

        Assert.True(result.Resumed);
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

        var result = control.Resume();

        Assert.True(result.Resumed);
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
        Assert.Equal("partial", control.Snapshot.Mode);
        Assert.False(control.Snapshot.RunningEncodesSuspended);
    }

    [Fact]
    public void A_signal_exception_for_one_encode_does_not_stop_the_others_from_being_suspended()
    {
        var encodes = new ActiveEncodeRegistry();
        encodes.Track(100, hardwareEncoder: false);
        encodes.Track(200, hardwareEncoder: false);
        var signals = new RecordingSignals(suspendOutcome: pid =>
            pid == 100 ? throw new InvalidOperationException("signal unavailable") : true);
        var control = Control(encodes, signals);

        control.Pause();

        Assert.True(control.IsPaused);
        Assert.Equal([100, 200], signals.Suspended.Order());
        Assert.Equal("partial", control.Snapshot.Mode);
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
        Assert.Equal("dispatchOnly", control.Snapshot.Mode);
        Assert.DoesNotContain("suspended", control.Snapshot.BlockedReason);
    }

    [Fact]
    public void Pause_with_signal_support_reports_encodes_as_suspended()
    {
        var encodes = new ActiveEncodeRegistry();
        encodes.Track(100, hardwareEncoder: false);
        var control = Control(encodes, new RecordingSignals());

        control.Pause();

        Assert.Equal("suspended", control.Snapshot.Mode);
        Assert.Contains("suspended", control.Snapshot.BlockedReason);
    }

    [Fact]
    public void Pause_with_no_running_encode_does_not_claim_that_one_was_suspended()
    {
        var control = Control(new ActiveEncodeRegistry(), new RecordingSignals());

        control.Pause();

        Assert.DoesNotContain("encodes are suspended", control.Snapshot.BlockedReason);
    }

    [Fact]
    public void A_resume_failure_keeps_dispatch_paused_and_reports_the_partial_state()
    {
        var encodes = new ActiveEncodeRegistry();
        encodes.Track(100, hardwareEncoder: false);
        var signals = new RecordingSignals(resumeOutcome: _ => false);
        var control = Control(encodes, signals);
        control.Pause();

        var result = control.Resume();

        Assert.False(result.Resumed);
        Assert.Equal(1, result.FailedEncodeCount);
        Assert.True(control.IsPaused);
        Assert.Equal("partial", control.Snapshot.Mode);
        Assert.Contains("could not be resumed", control.Snapshot.BlockedReason);
    }

    [Fact]
    public void A_resume_signal_exception_keeps_dispatch_paused_and_is_retryable()
    {
        var encodes = new ActiveEncodeRegistry();
        encodes.Track(100, hardwareEncoder: false);
        var throwOnResume = true;
        var signals = new RecordingSignals(resumeOutcome: _ =>
            throwOnResume ? throw new InvalidOperationException("signal unavailable") : true);
        var control = Control(encodes, signals);
        control.Pause();

        Assert.False(control.Resume().Resumed);
        throwOnResume = false;
        Assert.True(control.Resume().Resumed);

        Assert.False(control.IsPaused);
        Assert.Equal([100, 100], signals.Resumed);
    }

    [Fact]
    public void A_successfully_resumed_encode_is_not_signalled_again_when_resume_is_retried()
    {
        var encodes = new ActiveEncodeRegistry();
        encodes.Track(100, hardwareEncoder: false);
        encodes.Track(200, hardwareEncoder: false);
        var firstAttempt = true;
        var signals = new RecordingSignals(resumeOutcome: pid => pid != 200 || !firstAttempt);
        var control = Control(encodes, signals);
        control.Pause();

        Assert.False(control.Resume().Resumed);
        firstAttempt = false;
        Assert.True(control.Resume().Resumed);

        Assert.Equal(1, signals.Resumed.Count(pid => pid == 100));
        Assert.Equal(2, signals.Resumed.Count(pid => pid == 200));
        Assert.False(control.IsPaused);
    }

    [Fact]
    public void An_active_verification_is_disclosed_in_the_pause_reason()
    {
        var encodes = new ActiveEncodeRegistry();
        using var verification = encodes.TrackVerification();
        var control = Control(encodes, new RecordingSignals());

        control.Pause();

        Assert.Contains("Verification already in progress will finish", control.Snapshot.BlockedReason);
    }

    [Fact]
    public void Shutdown_resumes_suspended_encodes_without_reopening_dispatch()
    {
        var encodes = new ActiveEncodeRegistry();
        encodes.Track(100, hardwareEncoder: false);
        var signals = new RecordingSignals();
        var control = Control(encodes, signals);
        control.Pause();

        var result = control.ResumeProcessesForShutdown();

        Assert.True(result.Resumed);
        Assert.True(control.IsPaused);
        Assert.Equal([100], signals.Resumed);
    }
}
