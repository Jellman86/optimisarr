namespace Optimisarr.Core.Workers;

/// <summary>Reserves peak native work across simultaneous jobs in this process.</summary>
public sealed class PerceptualJobResourcePool
{
    public static PerceptualJobResourcePool Shared { get; } = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _disk = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<Guid, Charge> _owners = [];
    private long _memory;

    private sealed record Charge(string Root, long Disk, long Memory);

    /// <summary>Restore already-owed resources without treating today's lower capacity as retirement.</summary>
    public bool TryRestore(Guid owner, string root, long disk, long memory)
    {
        if (!Valid(owner, disk, memory)) return false;
        root = VolumeKey(root);
        lock (_gate)
        {
            if (_owners.TryGetValue(owner, out var existing))
                return _disk.Comparer.Equals(root, existing.Root) && disk == existing.Disk && memory == existing.Memory;
            return Add(owner, root, disk, memory);
        }
    }

    public bool TryReserve(Guid owner, string root, long disk, long memory, long? freeDisk, long? freeMemory)
    {
        if (!Valid(owner, disk, memory) || freeDisk is not { } availableDisk || freeMemory is not { } availableMemory) return false;
        root = VolumeKey(root);
        lock (_gate)
        {
            var reservedDisk = _disk.GetValueOrDefault(root);
            if (_owners.ContainsKey(owner) || availableDisk < reservedDisk || disk > availableDisk - reservedDisk
                || availableMemory < _memory || memory > availableMemory - _memory) return false;
            return Add(owner, root, disk, memory);
        }
    }

    /// <summary>The caller must first establish positive retirement through its ownership protocol.</summary>
    public bool Release(Guid owner)
    {
        lock (_gate)
        {
            if (!_owners.Remove(owner, out var charge)) return false;
            if (charge.Disk > 0)
            {
                var remaining = _disk[charge.Root] - charge.Disk;
                if (remaining == 0) _disk.Remove(charge.Root); else _disk[charge.Root] = remaining;
            }
            _memory -= charge.Memory;
            return true;
        }
    }

    private static bool Valid(Guid owner, long disk, long memory) =>
        owner != Guid.Empty && disk >= 0 && memory >= 0 && (disk > 0 || memory > 0);

    private static string VolumeKey(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    private bool Add(Guid owner, string root, long disk, long memory)
    {
        var reservedDisk = _disk.GetValueOrDefault(root);
        if (disk > long.MaxValue - reservedDisk || memory > long.MaxValue - _memory) return false;
        _owners.Add(owner, new(root, disk, memory));
        if (disk > 0) _disk[root] = reservedDisk + disk;
        _memory += memory;
        return true;
    }

    public (long? Disk, long? Memory) Available(string root, long? freeDisk, long? freeMemory)
    {
        lock (_gate)
            return (Remaining(freeDisk, _disk.GetValueOrDefault(VolumeKey(root))), Remaining(freeMemory, _memory));
    }
    private static long? Remaining(long? available, long reserved) => available is { } bytes ? bytes < reserved ? 0 : bytes - reserved : null;

    public IDisposable? TryAcquire(string root, long disk, long memory, long? freeDisk, long? freeMemory)
    {
        var owner = Guid.NewGuid();
        return TryReserve(owner, root, disk, memory, freeDisk, freeMemory) ? new Reservation(this, owner) : null;
    }

    private sealed class Reservation(PerceptualJobResourcePool pool, Guid owner) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            pool.Release(owner);
        }
    }
}
