namespace Optimisarr.Sidecar.Core.Session;

/// <summary>Reserves peak native work across simultaneous jobs in this worker process.</summary>
public sealed class PerceptualJobResourcePool
{
    public static PerceptualJobResourcePool Shared { get; } = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _disk = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private long _memory;

    public IDisposable? TryAcquire(string root, long disk, long memory, long? freeDisk, long? freeMemory)
    {
        if (disk <= 0 || memory <= 0 || freeDisk is not { } availableDisk || freeMemory is not { } availableMemory) return null;
        root = Path.GetFullPath(root);
        lock (_gate)
        {
            var reservedDisk = _disk.GetValueOrDefault(root);
            if (availableDisk < reservedDisk || disk > availableDisk - reservedDisk
                || availableMemory < _memory || memory > availableMemory - _memory) return null;
            _disk[root] = reservedDisk + disk; _memory += memory;
            return new Reservation(this, root, disk, memory);
        }
    }

    private sealed class Reservation(PerceptualJobResourcePool owner, string root, long disk, long memory) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (owner._gate)
            {
                var remaining = owner._disk[root] - disk;
                if (remaining == 0) owner._disk.Remove(root); else owner._disk[root] = remaining;
                owner._memory -= memory;
            }
        }
    }
}
