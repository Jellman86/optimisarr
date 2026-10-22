namespace Optimisarr.Core.Workers;

/// <summary>Reserves peak native work across simultaneous jobs in this process.</summary>
public sealed class PerceptualJobResourcePool
{
    public static PerceptualJobResourcePool Shared { get; } = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _disk = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<Guid, Charge> _owners = [];
    private long _memory;

    private sealed record Charge(string Volume, long Disk, long Memory);

    public bool TryReserve(Guid owner, string root, long disk, long memory, long? freeDisk, long? freeMemory)
    {
        if (!Valid(owner, disk, memory) || freeDisk is null || freeMemory is null) return false;
        var volume = VolumeKey(root);
        lock (_gate)
        {
            var reservedDisk = _disk.GetValueOrDefault(volume);
            if (_owners.ContainsKey(owner) || freeDisk < reservedDisk || disk > freeDisk - reservedDisk
                || freeMemory < _memory || memory > freeMemory - _memory) return false;
            if (disk > long.MaxValue - reservedDisk || memory > long.MaxValue - _memory) return false;
            _owners.Add(owner, new(volume, disk, memory));
            if (disk > 0) _disk[volume] = reservedDisk + disk;
            _memory += memory;
            return true;
        }
    }

    public bool Release(Guid owner)
    {
        lock (_gate)
        {
            if (!_owners.Remove(owner, out var charge)) return false;
            if (charge.Disk > 0)
            {
                var remaining = _disk[charge.Volume] - charge.Disk;
                if (remaining == 0) _disk.Remove(charge.Volume); else _disk[charge.Volume] = remaining;
            }
            _memory -= charge.Memory;
            return true;
        }
    }

    private static bool Valid(Guid owner, long disk, long memory) =>
        owner != Guid.Empty && disk >= 0 && memory >= 0 && (disk > 0 || memory > 0);

    private static string VolumeKey(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public (long? Disk, long? Memory) Available(string root, long? freeDisk, long? freeMemory)
    {
        var volume = VolumeKey(root);
        lock (_gate)
            return (Remaining(freeDisk, _disk.GetValueOrDefault(volume)), Remaining(freeMemory, _memory));
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
