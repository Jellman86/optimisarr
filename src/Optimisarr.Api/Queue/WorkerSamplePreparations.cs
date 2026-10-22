using System.Collections.Concurrent;
using Optimisarr.Data;

namespace Optimisarr.Api.Queue;

/// <summary>Either a ready sample, a refusal to report once, or neither while preparation runs.</summary>
public sealed record WorkerSampleAvailability(PreparedWorkerSample? Sample, string? Refusal);

/// <summary>
/// Prepares interactive worker samples outside the worker's claim request. Cutting a sample from a
/// very large original can take longer than the worker waits for a reply; a cancelled request must
/// not discard that work and start it again on the next poll.
/// </summary>
public sealed class WorkerSamplePreparations(IWorkerSampleSource source, CancellationToken stopping) : IDisposable
{
    private readonly ConcurrentDictionary<(int JobId, int Seconds, int Start), Task<PreparedWorkerSample>> _running = new();

    public WorkerSamplePreparations(IWorkerSampleSource source, IHostApplicationLifetime lifetime)
        : this(source, lifetime.ApplicationStopping) { }

    public WorkerSampleAvailability TryTake(Job job, int seconds, int start)
    {
        var key = (job.Id, seconds, start);
        var preparation = _running.GetOrAdd(key, _ => source.PrepareAsync(job, seconds, start, stopping));
        if (!preparation.IsCompleted) return new(null, null);
        // Taken once: a later claim for the same job prepares afresh, reusing the cached file.
        _running.TryRemove(new KeyValuePair<(int, int, int), Task<PreparedWorkerSample>>(key, preparation));
        if (preparation.IsCompletedSuccessfully) return new(preparation.Result, null);
        var error = preparation.Exception?.GetBaseException();
        return new(null, error is InvalidOperationException or IOException
            ? error.Message
            : "The worker's source sample could not be prepared.");
    }

    internal async Task WhenSettledAsync(Job job, int seconds, int start)
    {
        if (_running.TryGetValue((job.Id, seconds, start), out var preparation))
            try { await preparation; } catch (Exception) { }
    }

    public void Dispose() => _running.Clear();
}
