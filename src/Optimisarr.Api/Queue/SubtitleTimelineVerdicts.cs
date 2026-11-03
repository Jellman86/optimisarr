using System.Collections.Concurrent;
using Optimisarr.Core.Queue;

namespace Optimisarr.Api.Queue;

/// <summary>
/// Remembers whether an unchanged source's subtitle cues need Matroska. The scan demuxes the whole
/// file, which took 26 s for a 4K episode, and a worker claim prepares every candidate job, so
/// repeating it per job and per claim outlived the worker's 30-second request. The scan is not
/// bound to the asking request: a claim that gives up still leaves the answer for the next one.
/// </summary>
public sealed class SubtitleTimelineVerdicts(
    Func<string, IReadOnlyCollection<int>, CancellationToken, Task<bool>> probe,
    CancellationToken stopping)
{
    private const int MaximumRemembered = 512;
    private readonly ConcurrentDictionary<string, Task<bool>> _verdicts = new();

    public SubtitleTimelineVerdicts(SubtitleTimelineProbe probe, IHostApplicationLifetime lifetime)
        : this(probe.RequiresMatroskaAsync, lifetime.ApplicationStopping) { }

    public async Task<bool> RequiresMatroskaAsync(
        string path, IReadOnlyCollection<int> keptStreamIndexes, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists) return await probe(path, keptStreamIndexes, cancellationToken);
        // A changed source has a new length or modification time, so it is scanned afresh.
        var key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{string.Join(',', keptStreamIndexes.Order())}";
        if (_verdicts.Count >= MaximumRemembered) _verdicts.Clear();
        var verdict = _verdicts.GetOrAdd(key, _ => probe(path, keptStreamIndexes.ToArray(), stopping));
        try
        {
            return await verdict.WaitAsync(cancellationToken);
        }
        catch (Exception) when (verdict.IsFaulted || verdict.IsCanceled)
        {
            _verdicts.TryRemove(new KeyValuePair<string, Task<bool>>(key, verdict));
            throw;
        }
    }
}
