using System.Collections.Concurrent;

namespace Optimisarr.Api.Library;

/// <summary>A file a manager reported before it had settled, and when to look at it again.</summary>
/// <param name="ArrPath">The path exactly as the manager sent it, so the connection's mapping applies once.</param>
public sealed record SettlingWebhookFile(int ConnectionId, string ArrPath, DateTimeOffset DueAt, int Attempts);

/// <summary>
/// Files a webhook reported while they were still within the settling period. They are looked at again
/// once they settle rather than dropped. Held in memory and bounded: anything lost to a restart or a full
/// list is still found by the next library scan, which is the path this only shortens.
/// </summary>
public sealed class ArrWebhookSettlingQueue
{
    public const int Capacity = 1000;

    private readonly ConcurrentDictionary<(int ConnectionId, string ArrPath), SettlingWebhookFile> _files = new();

    public int Count => _files.Count;

    /// <summary>False when the list is full and this file is left to the next library scan.</summary>
    public bool Schedule(SettlingWebhookFile file)
    {
        var key = (file.ConnectionId, file.ArrPath);
        if (_files.Count >= Capacity && !_files.ContainsKey(key))
        {
            return false;
        }

        _files[key] = file;
        return true;
    }

    public IReadOnlyList<SettlingWebhookFile> TakeDue(DateTimeOffset nowUtc)
    {
        var due = new List<SettlingWebhookFile>();
        foreach (var (key, file) in _files)
        {
            if (file.DueAt <= nowUtc && _files.TryRemove(new KeyValuePair<(int, string), SettlingWebhookFile>(key, file)))
            {
                due.Add(file);
            }
        }
        return due;
    }
}

/// <summary>
/// Lets one webhook delivery work at a time, so a burst of imports probes files one after another
/// instead of all at once alongside the queue.
/// </summary>
public sealed class ArrWebhookGate
{
    public SemaphoreSlim Slot { get; } = new(1, 1);
}
