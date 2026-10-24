using System.Collections.Concurrent;
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
public sealed class MediaProbeBatches
{
    /// <summary>How many finished batches are kept for a late poll.</summary>
    private const int RetainedBatches = 20;

    /// <summary>How long a finished batch stays readable before it is forgotten.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    /// <summary>How long a batch may run before it is stopped, so no batch runs forever.</summary>
    public static readonly TimeSpan BatchTimeout = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<int, Batch> _batches = new();
    private readonly Channel<Batch> _queue = Channel.CreateUnbounded<Batch>();
    private int _nextId;

    /// <summary>Queue a batch of files to inspect again, and return how it stands.</summary>
    public MediaProbeBatchView Start(IReadOnlyList<int> mediaFileIds)
    {
        var batch = new Batch(Interlocked.Increment(ref _nextId), mediaFileIds);
        _batches[batch.Id] = batch;
        Prune();
        _queue.Writer.TryWrite(batch);
        return batch.View();
    }

    /// <summary>How a batch stands, or null when the batch is unknown or already forgotten.</summary>
    public MediaProbeBatchView? Get(int id) => _batches.TryGetValue(id, out var batch) ? batch.View() : null;

    /// <summary>Ask a running batch to stop after the file it is on.</summary>
    public bool Cancel(int id)
    {
        if (!_batches.TryGetValue(id, out var batch)) return false;
        return batch.RequestCancel();
    }

    /// <summary>Take the next batch to work on. Returns null when the queue is empty.</summary>
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
        if (_batches.Count <= RetainedBatches) return;
        var cutoff = DateTimeOffset.UtcNow - Retention;
        foreach (var entry in _batches)
        {
            if (entry.Value.FinishedAt is not null && entry.Value.FinishedAt < cutoff)
            {
                _batches.TryRemove(entry.Key, out _);
            }
        }

        // Still too many: the oldest finished batches go, so the registry stays bounded even when
        // several batches are started in a short while.
        foreach (var entry in _batches
            .Where(entry => entry.Value.FinishedAt is not null)
            .OrderBy(entry => entry.Value.FinishedAt))
        {
            if (_batches.Count <= RetainedBatches) break;
            _batches.TryRemove(entry.Key, out _);
        }
    }

    /// <summary>One batch of files, with the state the interface polls for.</summary>
    internal sealed class Batch(int id, IReadOnlyList<int> mediaFileIds)
    {
        private readonly List<MediaProbeBatchItem> _items = [];
        private readonly CancellationTokenSource _cancellation = new();
        private int _completed;
        private int _succeeded;
        private int _failed;
        private int _skipped;

        public int Id { get; } = id;

        public IReadOnlyList<int> MediaFileIds { get; } = mediaFileIds;

        public MediaProbeBatchState State { get; private set; } = MediaProbeBatchState.Queued;

        public DateTimeOffset? FinishedAt { get; private set; }

        public CancellationToken Token => _cancellation.Token;

        public void Start() => State = MediaProbeBatchState.Running;

        public bool RequestCancel()
        {
            if (State is MediaProbeBatchState.Completed or MediaProbeBatchState.Cancelled or MediaProbeBatchState.Failed)
            {
                return false;
            }

            _cancellation.Cancel();
            return true;
        }

        /// <summary>Record what became of one file.</summary>
        public void Record(int mediaFileId, MediaProbeOutcome outcome, string? error)
        {
            lock (_items)
            {
                _items.Add(new MediaProbeBatchItem(mediaFileId, outcome, error));
                _completed++;
                switch (outcome)
                {
                    case MediaProbeOutcome.Succeeded: _succeeded++; break;
                    case MediaProbeOutcome.Failed: _failed++; break;
                    case MediaProbeOutcome.Skipped: _skipped++; break;
                }
            }
        }

        public void Finish(MediaProbeBatchState state)
        {
            State = state;
            FinishedAt = DateTimeOffset.UtcNow;
        }

        public MediaProbeBatchView View()
        {
            lock (_items)
            {
                return new MediaProbeBatchView(
                    Id,
                    MediaFileIds.Count,
                    _completed,
                    _succeeded,
                    _failed,
                    _skipped,
                    State,
                    _items.ToArray());
            }
        }
    }
}
