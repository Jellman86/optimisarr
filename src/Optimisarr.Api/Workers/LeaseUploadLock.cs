namespace Optimisarr.Api.Workers;

/// <summary>
/// Serializes file operations for one lease, while other workers transfer independently.
/// Entries disappear after the final waiter, so finished assignments cannot grow this registry.
/// </summary>
internal static class LeaseUploadLock
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;
    }
    private static readonly Dictionary<Guid, Entry> Entries = [];

    public static async Task<IDisposable> AcquireAsync(Guid leaseId, CancellationToken token)
    {
        Entry entry;
        lock (Entries)
        {
            if (!Entries.TryGetValue(leaseId, out entry!)) Entries[leaseId] = entry = new Entry();
            entry.Users++;
        }
        try { await entry.Semaphore.WaitAsync(token); }
        catch { Release(leaseId, entry, acquired: false); throw; }
        return new Held(leaseId, entry);
    }

    private static void Release(Guid id, Entry entry, bool acquired)
    {
        lock (Entries)
        {
            if (acquired) entry.Semaphore.Release();
            if (--entry.Users == 0) { Entries.Remove(id); entry.Semaphore.Dispose(); }
        }
    }

    private sealed class Held(Guid id, Entry entry) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) Release(id, entry, acquired: true);
        }
    }
}
