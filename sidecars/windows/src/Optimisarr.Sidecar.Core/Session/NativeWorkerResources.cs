using Optimisarr.Core.Workers;
using Optimisarr.Sidecar.Core.Capabilities;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>Refresh volatile capacity without rerunning encoder and native-tool qualification.</summary>
public static class NativeWorkerResources
{
    public static SidecarCapabilities Refresh(SidecarCapabilities capabilities, string scratchRoot)
    {
        var resources = PerceptualJobRuntime.ReadResources(scratchRoot);
        var proof = capabilities.Perceptual ?? new WorkerPerceptualCapabilities();
        if (resources is null)
            return capabilities with { FreeScratchBytes = 0, Perceptual = proof with { AvailableMemoryBytes = null, ScratchUsesMemory = null } };
        var available = PerceptualJobResourcePool.Shared.Available(resources.Volume, resources.Disk, resources.Memory);
        return capabilities with { FreeScratchBytes = available.Disk ?? 0,
            Perceptual = proof with { AvailableMemoryBytes = available.Memory, ScratchUsesMemory = resources.UsesMemory } };
    }
}
