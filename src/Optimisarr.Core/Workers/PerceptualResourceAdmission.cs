using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Workers;

public sealed record NativeWorkingResources(string Volume, long? Disk, long? Memory, bool UsesMemory);

public sealed class PerceptualResourceAdmission(Func<string, NativeWorkingResources?> read, PerceptualJobResourcePool pool)
{
    public IDisposable? ReservePeak(MediaKind kind, string root)
    {
        var resources = read(root);
        var memory = PerceptualResourceBudget.MemoryBytes(kind, 1, false);
        return resources is not { Disk: > 0 } || memory is null ? null
            : pool.TryAcquire(resources.Volume, 0, memory.Value, resources.Disk, resources.Memory);
    }
    public IDisposable? ReserveWorkspace(long scratchBytes, string root)
    {
        if (scratchBytes <= 0) return null;
        var resources = read(root);
        return resources is null ? null : pool.TryAcquire(resources.Volume, scratchBytes,
            resources.UsesMemory ? scratchBytes : 0, resources.Disk, resources.Memory);
    }
}
