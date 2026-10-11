using System.Threading.Channels;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

public sealed record ExactDuplicateStatus(int LibraryId, string Status, DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt, ExactDuplicateProgress Progress, ExactDuplicateResult? Result, string? Error, bool IsStale = false);

/// <summary>One server scan at a time; completed file reads and snapshots survive restart.</summary>
public sealed class ExactDuplicateCoordinator(ExactDuplicateScanner scanner, ExactDuplicateStore store,
    ILogger<ExactDuplicateCoordinator>? logger = null) : BackgroundService
{
    private sealed record Work(long ScanId, int LibraryId, string Root, IReadOnlyList<ExactDuplicateInput> Inputs,
        bool ForceRefresh, CancellationTokenSource Cancel);
    private readonly object _lock = new();
    private readonly SemaphoreSlim _start = new(1);
    private readonly Channel<Work> _queue = Channel.CreateBounded<Work>(1);
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
        bool forceRefresh, CancellationToken token)
    {
        await _start.WaitAsync(token);
        try
        {
            lock (_lock) if (_active is not null) return false;
            var scanId = await store.TryCreateAsync(libraryId, root, inputs.Count, forceRefresh, token, inputs);
            if (scanId is null) return false;
            var queued = new ExactDuplicateStatus(libraryId, "Queued", DateTimeOffset.UtcNow, null,
                new(0, 0, inputs.Count, 0), null, null);
            lock (_lock)
            {
                _active = new(scanId.Value, libraryId, root, inputs, forceRefresh, new());
                _status = queued;
                return _queue.Writer.TryWrite(_active);
            }
        }
        finally { _start.Release(); }
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
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, work.Cancel.Token);
            var pending = new List<ExactDuplicateCheckpoint>(32);
            var state = ExactDuplicateScanState.Completed;
            string? error = null;
            var progress = new ExactDuplicateProgress(0, 0, work.Inputs.Count, 0);
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

    private void Update(Func<ExactDuplicateStatus, ExactDuplicateStatus> update)
    {
        lock (_lock) if (_status is not null) _status = update(_status);
    }
}
