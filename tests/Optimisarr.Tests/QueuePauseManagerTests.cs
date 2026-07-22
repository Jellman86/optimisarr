using Microsoft.Extensions.Logging.Abstractions;
using Optimisarr.Api.Queue;
using Optimisarr.Api.Realtime;

namespace Optimisarr.Tests;

public sealed class QueuePauseManagerTests
{
    private sealed class RecordingStore(List<string>? events = null) : IQueuePauseStateStore
    {
        public bool Paused { get; set; }
        public bool FailWrites { get; set; }
        public List<bool> Writes { get; } = [];
        public List<CancellationToken> WriteTokens { get; } = [];

        public Task<bool> GetPausedAsync(CancellationToken cancellationToken) => Task.FromResult(Paused);

        public Task SetPausedAsync(bool paused, CancellationToken cancellationToken)
        {
            events?.Add($"persist:{paused}");
            Writes.Add(paused);
            WriteTokens.Add(cancellationToken);
            if (FailWrites)
            {
                throw new IOException("database unavailable");
            }

            Paused = paused;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSignals(List<string>? events = null) : IProcessSignals
    {
        public bool Supported => true;
        public bool ResumeSucceeds { get; set; } = true;
        public List<int> Suspended { get; } = [];

        public bool TrySuspend(int pid)
        {
            events?.Add($"suspend:{pid}");
            Suspended.Add(pid);
            return true;
        }

        public bool TryResume(int pid)
        {
            events?.Add($"resume:{pid}");
            return ResumeSucceeds;
        }
    }

    private static (QueuePauseManager Manager, QueuePauseControl Control) Create(
        IQueuePauseStateStore store,
        IProcessSignals signals,
        ActiveEncodeRegistry? encodes = null)
    {
        var control = new QueuePauseControl(
            encodes ?? new ActiveEncodeRegistry(),
            signals,
            NullLogger<QueuePauseControl>.Instance);
        return (
            new QueuePauseManager(control, store, NullLogger<QueuePauseManager>.Instance),
            control);
    }

    [Fact]
    public async Task Pause_is_persisted_before_any_encode_is_suspended()
    {
        List<string> events = [];
        var encodes = new ActiveEncodeRegistry();
        encodes.Track(100, hardwareEncoder: false);
        var store = new RecordingStore(events);
        var (manager, _) = Create(store, new RecordingSignals(events), encodes);

        await manager.PauseAsync(CancellationToken.None);

        Assert.Equal(["persist:True", "suspend:100"], events);
    }

    [Fact]
    public async Task A_pause_persistence_failure_leaves_the_queue_and_encode_running()
    {
        var encodes = new ActiveEncodeRegistry();
        encodes.Track(100, hardwareEncoder: false);
        var store = new RecordingStore { FailWrites = true };
        var signals = new RecordingSignals();
        var (manager, control) = Create(store, signals, encodes);

        await Assert.ThrowsAsync<IOException>(() => manager.PauseAsync(CancellationToken.None));

        Assert.False(control.IsPaused);
        Assert.Empty(signals.Suspended);
    }

    [Fact]
    public async Task A_resume_signal_failure_keeps_the_durable_pause_and_dispatch_blocked()
    {
        var encodes = new ActiveEncodeRegistry();
        encodes.Track(100, hardwareEncoder: false);
        var store = new RecordingStore { Paused = true };
        var signals = new RecordingSignals { ResumeSucceeds = false };
        var (manager, control) = Create(store, signals, encodes);
        control.Pause();
        store.Writes.Clear();

        var result = await manager.ResumeAsync(CancellationToken.None);

        Assert.False(result.Resumed);
        Assert.True(control.IsPaused);
        Assert.True(store.Paused);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task A_resume_persistence_failure_suspends_the_encode_again()
    {
        var encodes = new ActiveEncodeRegistry();
        encodes.Track(100, hardwareEncoder: false);
        var store = new RecordingStore { Paused = true };
        var signals = new RecordingSignals();
        var (manager, control) = Create(store, signals, encodes);
        control.Pause();
        store.FailWrites = true;

        await Assert.ThrowsAsync<IOException>(() => manager.ResumeAsync(CancellationToken.None));

        Assert.True(control.IsPaused);
        Assert.Equal([100, 100], signals.Suspended);
    }

    [Fact]
    public async Task Resuming_an_unpaused_queue_is_an_idempotent_no_op_even_if_storage_is_unavailable()
    {
        var store = new RecordingStore { FailWrites = true };
        var (manager, control) = Create(store, new RecordingSignals());

        var result = await manager.ResumeAsync(CancellationToken.None);

        Assert.True(result.Resumed);
        Assert.False(control.IsPaused);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task State_writes_cannot_be_cancelled_after_a_transition_has_started()
    {
        var store = new RecordingStore();
        var (manager, _) = Create(store, new RecordingSignals());
        using var request = new CancellationTokenSource();

        await manager.PauseAsync(request.Token);

        Assert.False(store.WriteTokens.Single().CanBeCanceled);
    }

    [Fact]
    public async Task Automatic_work_never_starts_while_the_queue_is_paused()
    {
        var store = new RecordingStore();
        var (manager, _) = Create(store, new RecordingSignals());
        await manager.PauseAsync(CancellationToken.None);
        var ran = false;

        var result = await manager.TryRunAutomaticActionAsync(
            () =>
            {
                ran = true;
                return Task.FromResult("replaced");
            },
            CancellationToken.None);

        Assert.False(result.Started);
        Assert.False(ran);
    }

    [Fact]
    public async Task Pause_waits_for_an_already_started_automatic_action_to_finish()
    {
        var store = new RecordingStore();
        var (manager, _) = Create(store, new RecordingSignals());
        var actionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var action = manager.TryRunAutomaticActionAsync(
            async () =>
            {
                actionStarted.SetResult();
                await releaseAction.Task;
                return "replaced";
            },
            CancellationToken.None);
        await actionStarted.Task;

        var pause = manager.PauseAsync(CancellationToken.None);
        Assert.False(pause.IsCompleted);
        releaseAction.SetResult();
        await action;
        await pause;

        Assert.True(manager.IsPaused);
    }

    [Fact]
    public async Task Shutdown_release_does_not_clear_the_durable_pause()
    {
        var encodes = new ActiveEncodeRegistry();
        encodes.Track(100, hardwareEncoder: false);
        var store = new RecordingStore { Paused = true };
        var (manager, control) = Create(store, new RecordingSignals(), encodes);
        control.Pause();
        store.Writes.Clear();

        var result = await manager.ReleaseProcessesForShutdownAsync(CancellationToken.None);

        Assert.True(result.Resumed);
        Assert.True(manager.IsPaused);
        Assert.True(store.Paused);
        Assert.Empty(store.Writes);
    }
}
