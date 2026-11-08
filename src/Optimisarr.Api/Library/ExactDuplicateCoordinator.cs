using System.Threading.Channels;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

public sealed record ExactDuplicateStatus(int LibraryId, string Status, DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt, ExactDuplicateProgress Progress, ExactDuplicateResult? Result, string? Error, bool IsStale = false,
    IReadOnlyList<ExactDuplicateLibrary>? Libraries = null);
public sealed record ExactDuplicateLibrary(int Id, string Name, string Root, string MediaType);

/// <summary>One server scan at a time; completed file reads and snapshots survive restart.</summary>
public sealed class ExactDuplicateCoordinator(ExactDuplicateScanner scanner, ExactDuplicateStore store,
    ILogger<ExactDuplicateCoordinator>? logger = null) : BackgroundService
{
    private sealed record Work(long ScanId, int LibraryId, string Root, IReadOnlyList<ExactDuplicateInput> Inputs,
        bool ForceRefresh, CancellationTokenSource Cancel)
    {
        /// <summary>Taken by the executor; until then the work can be withdrawn without waiting for it.</summary>
        public bool Taken { get; set; }

        /// <summary>Withdrawn before the executor took it, so the executor skips it.</summary>
        public bool Withdrawn { get; set; }
    }
    private readonly object _lock = new();
    private readonly SemaphoreSlim _start = new(1);
    // Only one scan is active at a time; a withdrawn item can still sit here until the executor skips it.
    private readonly Channel<Work> _queue = Channel.CreateUnbounded<Work>();
    private Work? _active;
    private ExactDuplicateStatus? _status;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await store.RecoverAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    public async Task<ExactDuplicateStatus> ReadAsync(int libraryId, int page, CancellationToken token)
    {
        lock (_lock)
            if (_active?.LibraryId == libraryId && _status is not null) return _status;
        return await store.ReadAsync(libraryId, page, token);
    }

    public async Task<bool> TryStartAsync(int libraryId, string root, IReadOnlyList<ExactDuplicateInput> inputs,
        bool forceRefresh, CancellationToken token, IReadOnlyList<ExactDuplicateLibrary>? libraries = null) =>
        await TryStartScanAsync(libraryId, root, inputs, forceRefresh, token, libraries) is not null;

    /// <summary>The new scan's id, or null when another scan runs or the report changed from <paramref name="expect"/>.</summary>
    public async Task<long?> TryStartScanAsync(int libraryId, string root, IReadOnlyList<ExactDuplicateInput> inputs,
        bool forceRefresh, CancellationToken token, IReadOnlyList<ExactDuplicateLibrary>? libraries = null,
        ExactDuplicateScanExpectation? expect = null)
    {
        await _start.WaitAsync(token);
        try
        {
            lock (_lock) if (_active is not null) return null;
            var initial = InitialProgress(inputs);
            var scanId = await store.TryCreateAsync(libraryId, root, initial.Total, forceRefresh, token, inputs, libraries, expect);
            if (scanId is null) return null;
            var queued = new ExactDuplicateStatus(libraryId, "Queued", DateTimeOffset.UtcNow, null,
                initial, null, null, Libraries: libraries);
            lock (_lock)
            {
                _active = new(scanId.Value, libraryId, root, inputs, forceRefresh, new());
                _status = queued;
                return _queue.Writer.TryWrite(_active) ? scanId : null;
            }
        }
        finally { _start.Release(); }
    }

    /// <summary>The live status of the scan with this id, or null once it has stopped.</summary>
    public ExactDuplicateStatus? Active(long scanId)
    {
        lock (_lock) return _active?.ScanId == scanId ? _status : null;
    }

    /// <summary>How the scan with this id ended, or null when a newer scan has replaced it.</summary>
    public Task<ExactDuplicateScanState?> ReadStateAsync(long scanId, CancellationToken token) => store.StateAsync(scanId, token);

    /// <summary>
    /// Withdraws the scan with this id if the executor has not taken it yet, and frees its place so another scan can
    /// start. False when it has started, or has gone: a running scan is only ever stopped, never abandoned.
    /// </summary>
    public async Task<bool> WithdrawQueuedAsync(long scanId, CancellationToken token)
    {
        await _start.WaitAsync(token);
        try
        {
            Work work;
            lock (_lock)
            {
                if (_active is not { } active || active.ScanId != scanId || active.Taken) return false;
                active.Withdrawn = true;
                work = active;
                _active = null;
                _status = null;
            }
            work.Cancel.Cancel();
            work.Cancel.Dispose();
            await store.SetStateAsync(scanId, ExactDuplicateScanState.Cancelled, CancellationToken.None,
                "The scan waited too long to start and was withdrawn.");
            return true;
        }
        finally { _start.Release(); }
    }

    /// <summary>Stops the scan only while it is still the one with this id.</summary>
    public bool Cancel(int libraryId, long scanId)
    {
        lock (_lock)
        {
            if (_active?.LibraryId != libraryId || _active.ScanId != scanId) return false;
            _active.Cancel.Cancel(); return true;
        }
    }

    public bool Cancel(int libraryId)
    {
        lock (_lock)
        {
            if (_active?.LibraryId != libraryId) return false;
            _active.Cancel.Cancel(); return true;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var work in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            lock (_lock)
            {
                if (work.Withdrawn) continue;
                work.Taken = true;
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, work.Cancel.Token);
            var pending = new List<ExactDuplicateCheckpoint>(32);
            var state = ExactDuplicateScanState.Completed;
            string? error = null;
            var progress = InitialProgress(work.Inputs);
            try
            {
                await store.SetStateAsync(work.ScanId, ExactDuplicateScanState.Running, linked.Token);
                Update(s => s with { Status = "Running" });
                var cache = await store.CacheAsync(work.Inputs, linked.Token);
                await scanner.ScanAsync(work.Root, work.Inputs, p => { progress = p; Update(s => s with { Progress = p }); }, linked.Token,
                    cache, work.ForceRefresh, async (unit, token) =>
                    {
                        pending.Add(unit);
                        if (pending.Count < 32) return;
                        if (!await store.CheckpointAsync(work.ScanId, pending, token))
                        {
                            work.Cancel.Cancel();
                            token.ThrowIfCancellationRequested();
                        }
                        pending.Clear();
                    });
                linked.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                state = stoppingToken.IsCancellationRequested ? ExactDuplicateScanState.Interrupted : ExactDuplicateScanState.Cancelled;
            }
            catch (Exception e)
            {
                logger?.LogError(e, "Exact copy scan {ScanId} failed", work.ScanId);
                state = ExactDuplicateScanState.Failed;
                error = "The scan could not finish. Check the library path, permissions and server logs, then try again.";
            }
            finally
            {
                try
                {
                    // Only completed units are flushed. Cancelled scans never publish partial reports.
                    using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await store.CheckpointAsync(work.ScanId, pending, finish.Token);
                    await store.SetStateAsync(work.ScanId, state, finish.Token, error, progress);
                }
                finally { lock (_lock) { _active = null; _status = null; work.Cancel.Dispose(); } }
            }
        }
    }

    private static ExactDuplicateProgress InitialProgress(IReadOnlyList<ExactDuplicateInput> inputs)
    {
        var candidates = ExactDuplicateScanner.Candidates(inputs);
        return new(0, 0, candidates.Length, 0, 0, candidates.Sum(f => f.SizeBytes));
    }

    private void Update(Func<ExactDuplicateStatus, ExactDuplicateStatus> update)
    {
        lock (_lock) if (_status is not null) _status = update(_status);
    }
}
