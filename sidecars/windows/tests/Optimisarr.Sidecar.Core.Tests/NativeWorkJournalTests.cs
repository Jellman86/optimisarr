using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class NativeWorkJournalTests
{
    private static string Root => OperatingSystem.IsWindows() ? @"C:\owned" : "/owned";
    private static NativeWorkObligation Probe() => new(Guid.NewGuid(), NativeWorkPurpose.Capability, null, null,
        Root, new("root:1", "control:2", "gate:3"), "volume:1", 100, 200, false, NativeWorkPhase.Intent);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static NativeWorkSnapshot Add(NativeWorkSnapshot previous) => previous with {
        Revision = previous.Revision + 1, Obligations = [Probe()] };

    [Fact]
    public async Task Opening_reconfirms_existing_authority_before_publishing_a_snapshot()
    {
        var storage = new Storage();
        await using var journal = NativeWorkJournal.OpenExisting(storage);
        Assert.Equal(["binding", "ledger-read", "checkpoint-read", "ledger-reconfirm", "ledger-read", "checkpoint-write", "checkpoint-read",
            "ledger-read", "binding"], storage.Events);
        EqualSnapshot(storage.Initial, journal.Snapshot);
        Assert.False(storage.Disposed);
    }

    [Theory]
    [InlineData("missing-checkpoint")]
    [InlineData("missing-ledger")]
    [InlineData("wrong-binding")]
    [InlineData("old-ledger")]
    [InlineData("checkpoint-write")]
    [InlineData("ledger-reconfirm")]
    [InlineData("checkpoint-readback")]
    [InlineData("ledger-readback")]
    public void Opening_never_repairs_missing_changed_or_uncertain_authority(string failure)
    {
        var storage = new Storage();
        if (failure == "missing-checkpoint") storage.Checkpoint = [];
        if (failure == "missing-ledger") storage.Ledger = [];
        if (failure == "wrong-binding") storage.Binding = storage.Binding with { DirectoryIdentity = "changed" };
        if (failure == "old-ledger") {
            var next = NativeWorkLedger.Encode(Add(storage.Initial));
            storage.Checkpoint = NativeWorkCheckpointProtocol.Encode(NativeWorkCheckpointProtocol.Resolve(
                NativeWorkCheckpointProtocol.Begin(storage.Authority, storage.Ledger, next), storage.Binding, next));
        }
        if (failure == "ledger-reconfirm") storage.FailAt = 4;
        if (failure == "checkpoint-write") storage.FailAt = 6;
        if (failure == "checkpoint-readback") storage.CorruptAt = 7;
        if (failure == "ledger-readback") storage.CorruptAt = 8;
        var error = Record.Exception(() => NativeWorkJournal.OpenExisting(storage));
        Assert.True(error is IOException or InvalidDataException, error?.ToString());
        Assert.True(storage.Disposed);
    }

    private static void EqualSnapshot(NativeWorkSnapshot expected, NativeWorkSnapshot actual) =>
        Assert.Equal(NativeWorkLedger.Encode(expected), NativeWorkLedger.Encode(actual));

    [Fact]
    public async Task A_commit_publishes_only_after_pending_replace_and_committed_readbacks()
    {
        var storage = new Storage();
        await using var journal = NativeWorkJournal.OpenExisting(storage);
        storage.Reset();
        NativeWorkSnapshot? next = null;
        storage.OnStep = (_, _) => EqualSnapshot(storage.Initial, journal.Snapshot);
        await journal.ExecuteAsync<int>((scope, _) => {
            next = Add(scope.Snapshot); scope.Commit(next); return Task.CompletedTask;
        }, _ => Task.FromResult(42), (_, outcome) => {
            Assert.True(outcome.BodyCompleted); Assert.Equal(42, outcome.Result); return Task.CompletedTask;
        });
        Assert.Equal(["binding", "ledger-read", "checkpoint-read", "checkpoint-write", "checkpoint-read",
            "binding", "ledger-replace", "ledger-read", "binding", "checkpoint-write", "checkpoint-read",
            "ledger-read", "binding"], storage.Events);
        EqualSnapshot(next!, journal.Snapshot);
        Assert.Null(NativeWorkCheckpointProtocol.Decode(storage.Checkpoint).Pending);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    [InlineData(11)] [InlineData(12)] [InlineData(13)]
    public async Task Every_uncertain_commit_boundary_poison_fences_body_finalization_and_future_work(int boundary)
    {
        var storage = new Storage();
        await using var journal = NativeWorkJournal.OpenExisting(storage);
        storage.Reset(); storage.FailAt = boundary;
        var bodies = 0; var finalizers = 0;
        await Assert.ThrowsAnyAsync<IOException>(() => journal.ExecuteAsync<int>((scope, _) => {
            scope.Commit(Add(scope.Snapshot)); return Task.CompletedTask;
        }, _ => { bodies++; return Task.FromResult(1); }, (_, _) => { finalizers++; return Task.CompletedTask; }));
        Assert.True(journal.IsPoisoned);
        Assert.Throws<InvalidOperationException>(() => journal.Snapshot);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(journal));
        Assert.Equal(0, bodies); Assert.Equal(0, finalizers);
        Assert.False(storage.Disposed);
    }

    [Theory]
    [InlineData(5)] [InlineData(8)] [InlineData(11)] [InlineData(12)]
    public async Task A_successful_write_with_changed_readback_is_still_an_uncertain_commit(int boundary)
    {
        var storage = new Storage();
        await using var journal = NativeWorkJournal.OpenExisting(storage);
        storage.Reset(); storage.CorruptAt = boundary;
        await Assert.ThrowsAnyAsync<IOException>(() => journal.ExecuteAsync<int>((scope, _) => {
            scope.Commit(Add(scope.Snapshot)); return Task.CompletedTask;
        }, _ => Task.FromResult(1), (_, _) => Task.CompletedTask));
        Assert.True(journal.IsPoisoned);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Reopening_an_interrupted_commit_resolves_only_the_visible_authorized_old_or_new_snapshot(bool replaced)
    {
        var storage = new Storage();
        var next = Add(storage.Initial);
        storage.Checkpoint = NativeWorkCheckpointProtocol.Encode(NativeWorkCheckpointProtocol.Begin(storage.Authority,
            storage.Ledger, NativeWorkLedger.Encode(next)));
        if (replaced) storage.Ledger = NativeWorkLedger.Encode(next);
        await using var journal = NativeWorkJournal.OpenExisting(storage);
        EqualSnapshot(replaced ? next : storage.Initial, journal.Snapshot);
        Assert.Null(NativeWorkCheckpointProtocol.Decode(storage.Checkpoint).Pending);
    }

    [Fact]
    public async Task Invalid_in_memory_transition_does_not_poison_or_touch_storage()
    {
        var storage = new Storage();
        await using var journal = NativeWorkJournal.OpenExisting(storage);
        storage.Reset();
        await Assert.ThrowsAsync<InvalidDataException>(() => journal.ExecuteAsync<int>((scope, _) => {
            scope.Commit(scope.Snapshot with { Revision = 2 }); return Task.CompletedTask;
        }, _ => Task.FromResult(1), (_, outcome) => {
            Assert.False(outcome.PreparationCompleted); Assert.False(outcome.BodyStarted); return Task.CompletedTask;
        }));
        Assert.Empty(storage.Events); Assert.False(journal.IsPoisoned);
        Assert.Equal(1, await Run(journal));
    }

    [Fact]
    public async Task The_body_cannot_use_retained_preparation_or_finalization_write_authority()
    {
        var storage = new Storage();
        await using var journal = NativeWorkJournal.OpenExisting(storage);
        NativeWorkJournal.Session? prepared = null, finalized = null;
        await journal.ExecuteAsync<int>((scope, _) => { prepared = scope; return Task.CompletedTask; }, _ => {
            Assert.Throws<InvalidOperationException>(() => prepared!.Commit(Add(storage.Initial)));
            Assert.Throws<InvalidOperationException>(() => prepared!.Snapshot);
            return Task.FromResult(1);
        }, (scope, _) => { finalized = scope; return Task.CompletedTask; });
        await journal.ExecuteAsync<int>((_, _) => {
            Assert.Throws<InvalidOperationException>(() => finalized!.Commit(Add(storage.Initial)));
            return Task.CompletedTask;
        }, _ => Task.FromResult(1), (_, _) => Task.CompletedTask);
        Assert.False(journal.IsPoisoned);
    }

    [Fact]
    public async Task Shutdown_fences_queued_work_and_holds_ownership_until_actual_body_and_finalizer_finish()
    {
        var storage = new Storage();
        var journal = NativeWorkJournal.OpenExisting(storage);
        var body = Signal(); var releaseBody = Signal(); var finalizer = Signal(); var releaseFinalizer = Signal();
        var active = journal.ExecuteAsync<int>((_, _) => Task.CompletedTask, async token => {
            using var registration = token.Register(() => throw new InvalidOperationException("observer"));
            body.SetResult(); await releaseBody.Task; return 7;
        }, async (_, outcome) => {
            Assert.True(outcome.BodyStarted); finalizer.SetResult(); await releaseFinalizer.Task;
        });
        await body.Task;
        var queued = Run(journal);
        var close = journal.DisposeAsync().AsTask();
        Assert.False(close.IsCompleted); Assert.False(storage.Disposed);
        releaseBody.SetResult(); await finalizer.Task;
        Assert.False(close.IsCompleted); Assert.False(storage.Disposed);
        releaseFinalizer.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queued);
        await close;
        Assert.True(storage.Disposed);
        await journal.DisposeAsync(); Assert.Equal(1, storage.DisposeCount);
    }

    [Fact]
    public async Task Closing_during_authorization_skips_body_but_allows_positive_retirement_commits()
    {
        var storage = new Storage();
        var journal = NativeWorkJournal.OpenExisting(storage);
        Task? close = null;
        storage.Reset();
        storage.OnStep = (step, _) => { if (step == 10) close = journal.DisposeAsync().AsTask(); };
        var bodies = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.ExecuteAsync<int>((scope, _) => {
            scope.Commit(Add(scope.Snapshot)); return Task.CompletedTask;
        }, _ => { bodies++; return Task.FromResult(1); }, (scope, outcome) => {
            Assert.False(outcome.BodyStarted);
            var previous = scope.Snapshot;
            scope.Commit(previous with { Revision = previous.Revision + 1,
                Obligations = [previous.Obligations[0] with { Phase = NativeWorkPhase.Retired }] });
            return Task.CompletedTask;
        }));
        await close!;
        Assert.Equal(0, bodies); Assert.False(journal.IsPoisoned);
        Assert.Equal(NativeWorkPhase.Retired, NativeWorkLedger.Decode(storage.Ledger).Obligations[0].Phase);
    }

    [Fact]
    public async Task Preparation_body_and_finalizer_failures_preserve_original_error_and_finalization()
    {
        var storage = new Storage();
        await using var journal = NativeWorkJournal.OpenExisting(storage);
        var original = new IOException("body failed");
        var observed = await Assert.ThrowsAsync<IOException>(() => journal.ExecuteAsync<int>((_, _) => Task.CompletedTask,
            _ => Task.FromException<int>(original), (_, outcome) => {
                Assert.Same(original, outcome.Failure); Assert.False(outcome.BodyCompleted); return Task.CompletedTask;
            }));
        Assert.Same(original, observed); Assert.False(journal.IsPoisoned);
        var aggregate = await Assert.ThrowsAsync<AggregateException>(() => journal.ExecuteAsync<int>((_, _) => Task.CompletedTask,
            _ => Task.FromException<int>(original), (_, _) => throw new IOException("retirement failed")));
        Assert.Same(original, aggregate.InnerExceptions[0]); Assert.True(journal.IsPoisoned);
    }

    [Fact]
    public async Task Actual_operation_lifetimes_are_serialized_even_when_caller_cancellation_is_requested()
    {
        var storage = new Storage();
        await using var journal = NativeWorkJournal.OpenExisting(storage);
        using var cancellation = new CancellationTokenSource();
        var entered = Signal(); var release = Signal(); var secondEntered = false;
        var first = journal.ExecuteAsync<int>((_, _) => Task.CompletedTask, async _ => {
            entered.SetResult(); await release.Task; return 1;
        }, (_, _) => Task.CompletedTask, cancellation.Token);
        await entered.Task; cancellation.Cancel();
        var second = journal.ExecuteAsync<int>((_, _) => { secondEntered = true; return Task.CompletedTask; },
            _ => Task.FromResult(2), (_, _) => Task.CompletedTask);
        Assert.False(secondEntered); release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(2, await second);
    }

    [Theory]
    [InlineData(4)] [InlineData(7)] [InlineData(10)]
    public async Task A_persisted_write_with_lost_acknowledgement_holds_until_reopen_resolves_exact_authority(int boundary)
    {
        var storage = new Storage();
        var journal = NativeWorkJournal.OpenExisting(storage);
        storage.Reset(); storage.FailAfterWriteAt = boundary;
        await Assert.ThrowsAsync<IOException>(() => journal.ExecuteAsync<int>((scope, _) => {
            scope.Commit(Add(scope.Snapshot)); return Task.CompletedTask;
        }, _ => Task.FromResult(1), (_, _) => Task.CompletedTask));
        Assert.True(journal.IsPoisoned);
        await journal.DisposeAsync();
        var reopenedStorage = new Storage { Binding = storage.Binding, Ledger = storage.Ledger, Checkpoint = storage.Checkpoint };
        await using var reopened = NativeWorkJournal.OpenExisting(reopenedStorage);
        Assert.Equal(boundary == 4 ? 0 : 1, reopened.Snapshot.Revision);
        Assert.Null(NativeWorkCheckpointProtocol.Decode(reopenedStorage.Checkpoint).Pending);
    }

    [Fact]
    public async Task Closing_during_finalization_keeps_its_write_authority_until_positive_release_is_committed()
    {
        var storage = new Storage();
        var journal = NativeWorkJournal.OpenExisting(storage);
        var entered = Signal(); var finish = Signal();
        var active = journal.ExecuteAsync<int>((scope, _) => {
            scope.Commit(Add(scope.Snapshot)); return Task.CompletedTask;
        }, _ => Task.FromResult(1), async (scope, _) => {
            entered.SetResult(); await finish.Task;
            var previous = scope.Snapshot;
            scope.Commit(previous with { Revision = previous.Revision + 1,
                Obligations = [previous.Obligations[0] with { Phase = NativeWorkPhase.Retired }] });
            previous = scope.Snapshot;
            scope.Commit(previous with { Revision = previous.Revision + 1,
                Obligations = [previous.Obligations[0] with { Phase = NativeWorkPhase.Released }] });
        });
        await entered.Task;
        var closing = journal.DisposeAsync().AsTask();
        Assert.False(storage.Disposed); Assert.False(closing.IsCompleted);
        finish.SetResult(); await active; await closing;
        Assert.Equal(NativeWorkPhase.Released, NativeWorkLedger.Decode(storage.Ledger).Obligations[0].Phase);
    }

    [Fact]
    public async Task Preparation_failure_still_gets_one_finalization_with_the_last_committed_obligation()
    {
        var storage = new Storage();
        await using var journal = NativeWorkJournal.OpenExisting(storage);
        var failure = new IOException("workspace creation failed"); var bodies = 0; var finalized = 0;
        await Assert.ThrowsAsync<IOException>(() => journal.ExecuteAsync<int>((scope, _) => {
            scope.Commit(Add(scope.Snapshot)); throw failure;
        }, _ => { bodies++; return Task.FromResult(1); }, (scope, outcome) => {
            finalized++; Assert.False(outcome.PreparationCompleted); Assert.Same(failure, outcome.Failure);
            Assert.Single(scope.Snapshot.Obligations); return Task.CompletedTask;
        }));
        Assert.Equal(0, bodies); Assert.Equal(1, finalized);
    }

    [Fact]
    public async Task A_changed_binding_even_with_correct_bytes_poison_fences_new_effects()
    {
        var storage = new Storage();
        await using var journal = NativeWorkJournal.OpenExisting(storage);
        storage.Binding = storage.Binding with { CoordinatorIdentity = "replacement-lock" };
        await Assert.ThrowsAsync<IOException>(() => journal.ExecuteAsync<int>((scope, _) => {
            scope.Commit(Add(scope.Snapshot)); return Task.CompletedTask;
        }, _ => Task.FromResult(1), (_, _) => Task.CompletedTask));
        Assert.True(journal.IsPoisoned);
    }

    [Fact]
    public async Task Manufactured_sessions_cannot_write_during_body_or_after_storage_ownership_ends()
    {
        var storage = new Storage();
        var journal = NativeWorkJournal.OpenExisting(storage);
        storage.Reset();
        using var forged = new NativeWorkJournal.Session(journal, finalizing: true);
        await journal.ExecuteAsync<int>((_, _) => Task.CompletedTask, _ => {
            Assert.Throws<InvalidOperationException>(() => forged.Commit(Add(storage.Initial)));
            Assert.Throws<InvalidOperationException>(() => forged.Snapshot); return Task.FromResult(1);
        }, (scope, _) => {
            // Disposing forged authority must not revoke the actual finalization phase.
            forged.Dispose(); EqualSnapshot(storage.Initial, scope.Snapshot); return Task.CompletedTask;
        });
        await journal.DisposeAsync();
        using var afterClose = new NativeWorkJournal.Session(journal, finalizing: true);
        Assert.Throws<InvalidOperationException>(() => afterClose.Commit(Add(storage.Initial)));
        Assert.Empty(storage.Events);
    }

    [Fact]
    public async Task A_finalizer_cannot_report_success_by_catching_its_uncertain_commit()
    {
        var storage = new Storage();
        await using var journal = NativeWorkJournal.OpenExisting(storage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.ExecuteAsync<int>((_, _) => Task.CompletedTask,
            _ => Task.FromResult(42), (scope, _) => {
                storage.Reset(); storage.FailAt = 10;
                Assert.Throws<IOException>(() => scope.Commit(Add(scope.Snapshot)));
                return Task.CompletedTask;
            }));
        Assert.True(journal.IsPoisoned);
    }

    [Fact]
    public async Task Cancellation_observers_can_request_close_again_without_bypassing_finalization()
    {
        var storage = new Storage();
        var journal = NativeWorkJournal.OpenExisting(storage);
        var entered = Signal(); var observed = Signal(); var release = Signal(); var finalized = false;
        var active = journal.ExecuteAsync<int>((_, _) => Task.CompletedTask, async token => {
            using var registration = token.Register(() => { _ = journal.DisposeAsync(); observed.SetResult(); });
            entered.SetResult(); await release.Task; return 1;
        }, (_, _) => { finalized = true; return Task.CompletedTask; });
        await entered.Task;
        var closing = journal.DisposeAsync().AsTask();
        await observed.Task; Assert.False(storage.Disposed);
        release.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await closing; Assert.True(finalized); Assert.Equal(1, storage.DisposeCount);
    }

    private static Task<int> Run(NativeWorkJournal journal) => journal.ExecuteAsync<int>((_, _) => Task.CompletedTask,
        _ => Task.FromResult(1), (_, _) => Task.CompletedTask);

    private sealed class Storage : INativeWorkJournalStorage
    {
        public NativeWorkBinding Binding { get; set; } = new(Root, "directory:1", "lock:1");
        public NativeWorkSnapshot Initial { get; } = new(1, Guid.NewGuid(), 0, []);
        public NativeWorkCheckpoint Authority { get; }
        public byte[] Ledger { get; set; }
        public byte[] Checkpoint { get; set; }
        public List<string> Events { get; } = [];
        public int FailAt { get; set; }
        public int CorruptAt { get; set; }
        public int FailAfterWriteAt { get; set; }
        public Action<int, string>? OnStep { get; set; }
        public bool Disposed => DisposeCount > 0;
        public int DisposeCount { get; private set; }
        public Storage()
        {
            Ledger = NativeWorkLedger.Encode(Initial);
            Authority = NativeWorkCheckpointProtocol.Initialize(Binding, Ledger);
            Checkpoint = NativeWorkCheckpointProtocol.Encode(Authority);
        }
        public void Reset() { Events.Clear(); FailAt = 0; CorruptAt = 0; OnStep = null; }
        private void Step(string name)
        {
            Assert.False(Disposed); Events.Add(name); OnStep?.Invoke(Events.Count, name);
            if (Events.Count == FailAt) throw new IOException("injected " + name);
        }
        public void ValidateBinding() => Step("binding");
        public byte[] ReadLedger() { Step("ledger-read"); return Events.Count == CorruptAt ? [] : Ledger.ToArray(); }
        public byte[] ReadCheckpoint() { Step("checkpoint-read"); return Events.Count == CorruptAt ? [] : Checkpoint.ToArray(); }
        public void ReconfirmLedger(ReadOnlySpan<byte> expected) { Step("ledger-reconfirm"); Assert.Equal(expected.ToArray(), Ledger); }
        private void Written() { if (Events.Count == FailAfterWriteAt) throw new IOException("persisted without acknowledgement"); }
        public void WriteCheckpoint(ReadOnlySpan<byte> bytes) { Step("checkpoint-write"); Checkpoint = bytes.ToArray(); Written(); }
        public void ReplaceLedger(ReadOnlySpan<byte> bytes) { Step("ledger-replace"); Ledger = bytes.ToArray(); Written(); }
        public void Dispose() { DisposeCount++; }
    }
}
