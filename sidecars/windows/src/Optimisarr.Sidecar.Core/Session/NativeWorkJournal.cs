using System.Runtime.ExceptionServices;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>
/// Owns an exclusively locked, independently anchored store. Implementations must bound reads,
/// validate pinned private identities, and durably synchronize each write before returning.
/// The lock is non-inherited and remains held until Dispose; missing authority is never repaired.
/// </summary>
internal interface INativeWorkJournalStorage : IDisposable
{
    NativeWorkBinding Binding { get; }
    void ValidateBinding();
    byte[] ReadLedger();
    byte[] ReadCheckpoint();
    void ReconfirmLedger(ReadOnlySpan<byte> expected);
    void WriteCheckpoint(ReadOnlySpan<byte> bytes);
    void ReplaceLedger(ReadOnlySpan<byte> bytes);
}

internal sealed record NativeWorkExecutionOutcome<T>(bool PreparationCompleted, bool BodyStarted,
    bool BodyCompleted, T? Result, Exception? Failure);

/// <summary>
/// Serializes the actual preparation, body and retirement lifetimes. Preparation/finalization
/// must join their own work; only the body may run media children, and must join them before returning.
/// Physical cleanup proof and resource release remain the coordinator's responsibility.
/// </summary>
internal sealed class NativeWorkJournal : IAsyncDisposable
{
    private readonly INativeWorkJournalStorage _storage;
    private readonly NativeWorkBinding _binding;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _closeSync = new(), _cancelSync = new();
    private NativeWorkSnapshot _snapshot;
    private NativeWorkCheckpoint _checkpoint;
    private byte[] _bytes;
    private Session? _activeSession;
    private int _closing, _poisoned;
    private Task? _closeTask, _cancellationTask;

    private NativeWorkJournal(INativeWorkJournalStorage storage, NativeWorkSnapshot snapshot,
        NativeWorkCheckpoint checkpoint, byte[] bytes)
    {
        _storage = storage; _binding = checkpoint.Binding; _snapshot = snapshot;
        _checkpoint = checkpoint; _bytes = bytes;
    }

    public bool IsPoisoned => Volatile.Read(ref _poisoned) != 0;
    public NativeWorkSnapshot Snapshot { get { RequireAvailable(finalizing: false); return Volatile.Read(ref _snapshot); } }

    public static NativeWorkJournal OpenExisting(INativeWorkJournalStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        try
        {
            storage.ValidateBinding();
            var binding = storage.Binding;
            var bytes = storage.ReadLedger();
            var snapshot = NativeWorkLedger.Decode(bytes);
            var checkpoint = NativeWorkCheckpointProtocol.Resolve(
                NativeWorkCheckpointProtocol.Decode(storage.ReadCheckpoint()), binding, bytes);
            var authority = NativeWorkCheckpointProtocol.Encode(checkpoint);
            // A rename may have become visible before its directory sync failed. Persist that
            // exact pinned generation before discarding old/new checkpoint recovery authority.
            storage.ReconfirmLedger(bytes);
            Exact(storage.ReadLedger(), bytes);
            // Reconfirm even an unchanged checkpoint: visibility is not durability after a crash.
            storage.WriteCheckpoint(authority);
            Exact(storage.ReadCheckpoint(), authority);
            Exact(storage.ReadLedger(), bytes);
            storage.ValidateBinding();
            if (storage.Binding != binding) throw Changed();
            return new(storage, snapshot, checkpoint, bytes.ToArray());
        }
        catch { storage.Dispose(); throw; }
    }

    public async Task<T> ExecuteAsync<T>(Func<Session, CancellationToken, Task> prepare,
        Func<CancellationToken, Task<T>> body, Func<Session, NativeWorkExecutionOutcome<T>, Task> finalize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepare); ArgumentNullException.ThrowIfNull(body); ArgumentNullException.ThrowIfNull(finalize);
        RequireAvailable(finalizing: false);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireAvailable(finalizing: false);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            var outcome = new NativeWorkExecutionOutcome<T>(false, false, false, default, null);
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                using (var scope = BeginPhase(finalizing: false))
                    await prepare(scope, cancellation.Token).ConfigureAwait(false);
                outcome = outcome with { PreparationCompleted = true };
                _stop.Token.ThrowIfCancellationRequested();
                cancellation.Token.ThrowIfCancellationRequested();
                RequireAvailable(finalizing: false);
                outcome = outcome with { BodyStarted = true };
                var result = await body(cancellation.Token).ConfigureAwait(false);
                outcome = outcome with { BodyCompleted = true, Result = result };
                _stop.Token.ThrowIfCancellationRequested();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            catch (Exception error) { outcome = outcome with { Failure = error }; }

            // An uncertain commit grants no further effects. Ordinary cancellation/failure still
            // runs retirement under exclusive ownership, independently of caller cancellation.
            if (!IsPoisoned)
            {
                try
                {
                    using var scope = BeginPhase(finalizing: true);
                    await finalize(scope, outcome).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    Poison();
                    outcome = outcome with { Failure = outcome.Failure is null ? error : new AggregateException(outcome.Failure, error) };
                }
            }
            if (outcome.Failure is not null) ExceptionDispatchInfo.Capture(outcome.Failure).Throw();
            // A callback can catch a failed commit, but cannot convert uncertain accounting
            // into a successfully completed operation.
            RequireAvailable(finalizing: true);
            return outcome.Result!;
        }
        finally { _operation.Release(); }
    }

    private void Commit(NativeWorkSnapshot next, bool finalizing)
    {
        RequireAvailable(finalizing);
        // Invalid caller transitions have no I/O effects and do not destroy trusted authority.
        var bytes = NativeWorkLedger.Encode(next);
        var pending = NativeWorkCheckpointProtocol.Begin(_checkpoint, _bytes, bytes);
        var pendingBytes = NativeWorkCheckpointProtocol.Encode(pending);
        var committed = NativeWorkCheckpointProtocol.Resolve(pending, _binding, bytes);
        var committedBytes = NativeWorkCheckpointProtocol.Encode(committed);
        try
        {
            ValidateBinding();
            Exact(_storage.ReadLedger(), _bytes);
            Exact(_storage.ReadCheckpoint(), NativeWorkCheckpointProtocol.Encode(_checkpoint));
            _storage.WriteCheckpoint(pendingBytes);
            Exact(_storage.ReadCheckpoint(), pendingBytes);
            ValidateBinding();
            _storage.ReplaceLedger(bytes);
            Exact(_storage.ReadLedger(), bytes);
            ValidateBinding();
            _storage.WriteCheckpoint(committedBytes);
            Exact(_storage.ReadCheckpoint(), committedBytes);
            Exact(_storage.ReadLedger(), bytes);
            ValidateBinding();
            _checkpoint = committed; _bytes = bytes;
            Volatile.Write(ref _snapshot, next);
        }
        catch { Poison(); throw; }
    }

    private void ValidateBinding()
    {
        _storage.ValidateBinding();
        if (_storage.Binding != _binding) throw Changed();
    }

    private void RequireAvailable(bool finalizing)
    {
        if (IsPoisoned) throw new InvalidOperationException("Native work accounting requires trusted recovery before further effects.");
        if (!finalizing && Volatile.Read(ref _closing) != 0) throw new InvalidOperationException("Native work accounting is closing.");
    }

    private static void Exact(byte[] visible, byte[] expected)
    { if (!visible.AsSpan().SequenceEqual(expected)) throw Changed(); }
    private static IOException Changed() => new("Native work accounting readback or physical binding changed.");
    private void Poison() { Interlocked.Exchange(ref _poisoned, 1); RequestCancellation(); }

    private Session BeginPhase(bool finalizing)
    {
        var session = new Session(this, finalizing);
        Volatile.Write(ref _activeSession, session);
        return session;
    }

    private void RequestCancellation()
    {
        lock (_cancelSync) _cancellationTask ??= ObserveCancellationAsync(_stop.CancelAsync());
    }
    private static async Task ObserveCancellationAsync(Task callbacks)
    {
        // Cancellation observers are advisory. They cannot skip retirement or dispose ownership.
        try { await callbacks.ConfigureAwait(false); } catch (Exception) { }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _closing, 1);
        RequestCancellation();
        lock (_closeSync) return new(_closeTask ??= CloseAsync());
    }

    private async Task CloseAsync()
    {
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            Task callbacks;
            lock (_cancelSync) callbacks = _cancellationTask!;
            await callbacks.ConfigureAwait(false);
            _storage.Dispose(); _stop.Dispose();
        }
        finally { _operation.Release(); }
    }

    internal sealed class Session(NativeWorkJournal journal, bool finalizing) : IDisposable
    {
        private readonly object _sync = new();
        private bool _ended;
        public NativeWorkSnapshot Snapshot
        {
            get { lock (_sync) { RequireActive(); return Volatile.Read(ref journal._snapshot); } }
        }
        public void Commit(NativeWorkSnapshot next)
        { lock (_sync) { RequireActive(); journal.Commit(next, finalizing); } }
        private void RequireActive()
        {
            if (_ended || !ReferenceEquals(Volatile.Read(ref journal._activeSession), this))
                throw new InvalidOperationException("Native work accounting phase has ended or was not admitted.");
            journal.RequireAvailable(finalizing);
        }
        // Joins a synchronous commit that began before its phase returned, including a retained
        // continuation racing phase completion. The body never overlaps a writable phase.
        public void Dispose()
        {
            lock (_sync) { _ended = true; Interlocked.CompareExchange(ref journal._activeSession, null, this); }
        }
    }
}
