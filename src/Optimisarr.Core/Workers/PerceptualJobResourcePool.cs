namespace Optimisarr.Core.Workers;

/// <summary>Reserves peak native work across simultaneous jobs in this process.</summary>
public sealed class PerceptualJobResourcePool
{
    public static PerceptualJobResourcePool Shared { get; } = new();
    private readonly object _gate = new();
    private readonly bool _physicalVolumes;
    private readonly Dictionary<string, long> _disk;
    private readonly Dictionary<Guid, Charge> _owners = [];
    private long _memory;

    private sealed record Charge(string Volume, long Disk, long Memory);

    public PerceptualJobResourcePool() : this(false) { }
    private PerceptualJobResourcePool(bool physicalVolumes)
    {
        _physicalVolumes = physicalVolumes;
        _disk = new(!physicalVolumes && OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    /// <summary>Fix one physical-ID namespace; path calls fail closed rather than hiding debt.</summary>
    public static PerceptualJobResourcePool ForPhysicalVolumes() => new(true);

    /// <summary>Restore already-owed resources without treating today's lower capacity as retirement.</summary>
    public bool TryRestore(Guid owner, string root, long disk, long memory)
    {
        if (_physicalVolumes || !Valid(owner, disk, memory)) return false;
        return Restore(owner, VolumeKey(root), disk, memory);
    }

    public bool TryRestore(Guid owner, PhysicalVolumeId volume, long disk, long memory)
    {
        if (!_physicalVolumes || volume is not { IsValid: true } || !Valid(owner, disk, memory)) return false;
        return Restore(owner, volume.Value, disk, memory);
    }

    private bool Restore(Guid owner, string volume, long disk, long memory)
    {
        lock (_gate)
        {
            if (_owners.TryGetValue(owner, out var existing))
                return _disk.Comparer.Equals(volume, existing.Volume) && disk == existing.Disk && memory == existing.Memory;
            return Add(owner, volume, disk, memory);
        }
    }

    public bool TryReserve(Guid owner, string root, long disk, long memory, long? freeDisk, long? freeMemory)
    {
        if (_physicalVolumes || !Valid(owner, disk, memory) || freeDisk is null || freeMemory is null) return false;
        return Reserve(owner, VolumeKey(root), disk, memory, freeDisk.Value, freeMemory.Value);
    }

    public bool TryReserve(Guid owner, PhysicalVolumeId volume, long disk, long memory, long? freeDisk, long? freeMemory)
    {
        if (!_physicalVolumes || volume is not { IsValid: true } || !Valid(owner, disk, memory) || freeDisk is null || freeMemory is null) return false;
        return Reserve(owner, volume.Value, disk, memory, freeDisk.Value, freeMemory.Value);
    }

    private bool Reserve(Guid owner, string volume, long disk, long memory, long availableDisk, long availableMemory)
    {
        lock (_gate)
        {
            var reservedDisk = _disk.GetValueOrDefault(volume);
            if (_owners.ContainsKey(owner) || availableDisk < reservedDisk || disk > availableDisk - reservedDisk
                || availableMemory < _memory || memory > availableMemory - _memory) return false;
            return Add(owner, volume, disk, memory);
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

    private bool Add(Guid owner, string volume, long disk, long memory)
    {
        var reservedDisk = _disk.GetValueOrDefault(volume);
        if (disk > long.MaxValue - reservedDisk || memory > long.MaxValue - _memory) return false;
        _owners.Add(owner, new(volume, disk, memory));
        if (disk > 0) _disk[volume] = reservedDisk + disk;
        _memory += memory;
        return true;
    }

    public (long? Disk, long? Memory) Available(string root, long? freeDisk, long? freeMemory)
    {
        if (_physicalVolumes) return (0, 0);
        return AvailableKey(VolumeKey(root), freeDisk, freeMemory);
    }

    public (long? Disk, long? Memory) Available(PhysicalVolumeId volume, long? freeDisk, long? freeMemory)
    {
        if (!_physicalVolumes || volume is not { IsValid: true }) return (0, 0);
        return AvailableKey(volume.Value, freeDisk, freeMemory);
    }

    private (long? Disk, long? Memory) AvailableKey(string volume, long? freeDisk, long? freeMemory)
    {
        lock (_gate)
            return (Remaining(freeDisk, _disk.GetValueOrDefault(volume)), Remaining(freeMemory, _memory));
    }
    private static long? Remaining(long? available, long reserved) => available is { } bytes ? bytes < reserved ? 0 : bytes - reserved : null;

    public IDisposable? TryAcquire(string root, long disk, long memory, long? freeDisk, long? freeMemory)
    {
        var owner = Guid.NewGuid();
        return TryReserve(owner, root, disk, memory, freeDisk, freeMemory) ? new Reservation(this, owner) : null;
    }

    public IDisposable? TryAcquire(PhysicalVolumeId volume, long disk, long memory, long? freeDisk, long? freeMemory)
    {
        var owner = Guid.NewGuid();
        return TryReserve(owner, volume, disk, memory, freeDisk, freeMemory) ? new Reservation(this, owner) : null;
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
