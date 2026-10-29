using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Optimisarr.Api.Library;

/// <summary>What became of one file in a probe batch.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MediaProbeOutcome
{
    /// <summary>The file was inspected again and its probe result was refreshed.</summary>
    Succeeded,

    /// <summary>The file exists but could not be inspected; the probe error says why.</summary>
    Failed,

    /// <summary>There is no such file any more, so there was nothing to inspect.</summary>
    Skipped
}

/// <summary>One file of a probe batch and what became of it.</summary>
public sealed record MediaProbeBatchItem(int MediaFileId, MediaProbeOutcome Outcome, string? Error);

/// <summary>How a batch stands: it is queued, running, or finished one way or another.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MediaProbeBatchState
{
    Queued,
    Running,
    Completed,
    Cancelled,
    Failed
}

/// <summary>
/// A batch of files to inspect again, as the operator sees it: how far it has got, how many files
/// were inspected, how many failed, and what happened to each one.
/// </summary>
public sealed record MediaProbeBatchView(
    int Id,
    int Total,
    int Completed,
    int Succeeded,
    int Failed,
    int Skipped,
    MediaProbeBatchState State,
    IReadOnlyList<MediaProbeBatchItem> Items);

/// <summary>
/// The registry of operator-requested probe batches. A batch is work the operator asked for, so it
/// is tracked rather than fire-and-forget: the interface polls it for progress and reports what
/// each file became. Finished batches are kept for a while so a late poll still finds them, then
/// forgotten — a batch is a record of one gesture, not a ledger.
/// </summary>
public sealed class MediaProbeBatches(TimeProvider clock)
{
    public MediaProbeBatches() : this(TimeProvider.System) { }

    private const int RetainedBatches = 20;
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);
    public static readonly TimeSpan BatchTimeout = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private readonly Dictionary<int, Batch> _batches = [];
    private readonly Channel<Batch> _queue = Channel.CreateBounded<Batch>(RetainedBatches);
    private int _nextId;

    /// <summary>Admit one bounded batch, or refuse it without evicting live work.</summary>
    public MediaProbeBatchView? Start(IReadOnlyList<int> mediaFileIds)
    {
        lock (_gate)
        {
            Prune();
            if (_batches.Count >= RetainedBatches)
            {
                var oldest = _batches.Values.Where(batch => batch.FinishedAt is not null)
                    .OrderBy(batch => batch.FinishedAt).FirstOrDefault();
                if (oldest is null) return null;
                _batches.Remove(oldest.Id);
            }

            var batch = new Batch(++_nextId, mediaFileIds.ToArray(), clock);
            if (!_queue.Writer.TryWrite(batch)) return null;
            _batches.Add(batch.Id, batch);
            return batch.View();
        }
    }

    public MediaProbeBatchView? Get(int id)
    {
        lock (_gate)
        {
            Prune();
            return _batches.TryGetValue(id, out var batch) ? batch.View() : null;
        }
    }

    /// <summary>Stop queued work now; a running file finishes before cancellation takes effect.</summary>
    public bool Cancel(int id)
    {
        lock (_gate)
        {
            return _batches.TryGetValue(id, out var batch) && batch.RequestCancel();
        }
    }

    internal async Task<Batch?> TakeAsync(CancellationToken cancellationToken)
    {
        while (await _queue.Reader.WaitToReadAsync(cancellationToken))
        {
            if (_queue.Reader.TryRead(out var batch)) return batch;
        }
        return null;
    }

    private void Prune()
    {
        var cutoff = clock.GetUtcNow() - Retention;
        foreach (var batch in _batches.Values.Where(batch => batch.FinishedAt <= cutoff).ToArray())
            _batches.Remove(batch.Id);
    }

    internal sealed class Batch(int id, IReadOnlyList<int> mediaFileIds, TimeProvider clock)
    {
        private readonly object _gate = new();
        private readonly List<MediaProbeBatchItem> _items = [];
        private MediaProbeBatchState _state = MediaProbeBatchState.Queued;
        private DateTimeOffset? _finishedAt;
        private bool _cancelRequested;

        public int Id { get; } = id;
        public IReadOnlyList<int> MediaFileIds { get; } = mediaFileIds;
        public DateTimeOffset? FinishedAt { get { lock (_gate) return _finishedAt; } }
        public bool CancelRequested { get { lock (_gate) return _cancelRequested; } }

        public bool TryStart()
        {
            lock (_gate)
            {
                if (_state != MediaProbeBatchState.Queued) return false;
                _state = MediaProbeBatchState.Running;
                return true;
            }
        }

        public bool RequestCancel()
        {
            lock (_gate)
            {
                if (_state is not (MediaProbeBatchState.Queued or MediaProbeBatchState.Running)) return false;
                _cancelRequested = true;
                if (_state == MediaProbeBatchState.Queued) Finish(MediaProbeBatchState.Cancelled);
                return true;
            }
        }

        public void Record(int mediaFileId, MediaProbeOutcome outcome, string? error)
        {
            lock (_gate) _items.Add(new MediaProbeBatchItem(mediaFileId, outcome, error));
        }

        public void Finish(MediaProbeBatchState state)
        {
            lock (_gate)
            {
                _state = state;
                _finishedAt = clock.GetUtcNow();
            }
        }

        public MediaProbeBatchView View()
        {
            lock (_gate)
            {
                return new(Id, MediaFileIds.Count, _items.Count,
                    _items.Count(item => item.Outcome == MediaProbeOutcome.Succeeded),
                    _items.Count(item => item.Outcome == MediaProbeOutcome.Failed),
                    _items.Count(item => item.Outcome == MediaProbeOutcome.Skipped), _state, _items.ToArray());
            }
        }
    }
}
