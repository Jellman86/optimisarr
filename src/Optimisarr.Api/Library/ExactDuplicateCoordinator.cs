using System.Threading.Channels;

namespace Optimisarr.Api.Library;

public sealed record ExactDuplicateStatus(int LibraryId, string Status, DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt, ExactDuplicateProgress Progress, ExactDuplicateResult? Result, string? Error);

/// <summary>One explicit scan at a time. Keeps the last ten library snapshots until restart.
/// Hosted cancellation owns every read; leaving the review page does not kill a scan.</summary>
public sealed class ExactDuplicateCoordinator(ExactDuplicateScanner scanner) : BackgroundService
{
    private sealed record Work(int LibraryId, string Root, IReadOnlyList<ExactDuplicateInput> Inputs, CancellationTokenSource Cancel);
    private readonly object _lock = new();
    private readonly Channel<Work> _queue = Channel.CreateBounded<Work>(1);
    private readonly Dictionary<int, ExactDuplicateStatus> _reports = [];
    private Work? _active;

    public ExactDuplicateStatus Read(int libraryId)
    {
        lock (_lock) return _reports.GetValueOrDefault(libraryId)
            ?? new(libraryId, "NotStarted", null, null, new(0, 0, 0, 0), null, null);
    }
    public bool TryStart(int libraryId, string root, IReadOnlyList<ExactDuplicateInput> inputs)
    {
        lock (_lock)
        {
            if (_active is not null) return false;
            _active = new(libraryId, root, inputs, new());
            if (_reports.Count >= 10 && !_reports.ContainsKey(libraryId))
                _reports.Remove(_reports.MinBy(p => p.Value.StartedAt).Key);
            _reports[libraryId] = new(libraryId, "Queued", DateTimeOffset.UtcNow, null, new(0, 0, 0, 0), null, null);
            return _queue.Writer.TryWrite(_active);
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
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, work.Cancel.Token);
            try
            {
                Update(work.LibraryId, s => s with { Status = "Running" });
                var result = await scanner.ScanAsync(work.Root, work.Inputs,
                    p => Update(work.LibraryId, s => s with { Progress = p }), linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                Update(work.LibraryId, s => s with { Status = "Completed", FinishedAt = DateTimeOffset.UtcNow, Result = result });
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                Update(work.LibraryId, s => s with { Status = "Cancelled", FinishedAt = DateTimeOffset.UtcNow });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Update(work.LibraryId, s => s with
                {
                    Status = "Failed",
                    FinishedAt = DateTimeOffset.UtcNow,
                    Error = "The library could not be read. Check its path and permissions, then try again."
                });
            }
            finally
            {
                lock (_lock) { _active = null; work.Cancel.Dispose(); }
            }
        }
    }
    private void Update(int id, Func<ExactDuplicateStatus, ExactDuplicateStatus> update)
    {
        lock (_lock) _reports[id] = update(_reports[id]);
    }
}
