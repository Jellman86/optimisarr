using Optimisarr.Core.Workers;
using Optimisarr.Sidecar.Core.Capabilities;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>Refresh volatile capacity without rerunning encoder and native-tool qualification.</summary>
public static class NativeWorkerResources
{
    public static SidecarCapabilities Refresh(SidecarCapabilities capabilities, string scratchRoot)
    {
        var resources = PerceptualJobRuntime.ReadResources(scratchRoot);
        if (resources is null)
            return Unavailable(capabilities);
        var proof = capabilities.Perceptual ?? new WorkerPerceptualCapabilities();
        var available = PerceptualJobResourcePool.Shared.Available(resources.Volume, resources.Disk, resources.Memory);
        return capabilities with { FreeScratchBytes = available.Disk ?? 0,
            Perceptual = proof with { AvailableMemoryBytes = available.Memory, ScratchUsesMemory = resources.UsesMemory } };
    }

    /// <summary>
    /// No working space to offer: its volume cannot be read, or no usable location is configured.
    /// The server offers nothing to a worker reporting this, whatever it can encode.
    /// </summary>
    public static SidecarCapabilities Unavailable(SidecarCapabilities capabilities) =>
        capabilities with { FreeScratchBytes = 0, Perceptual = (capabilities.Perceptual ?? new WorkerPerceptualCapabilities())
            with { AvailableMemoryBytes = null, ScratchUsesMemory = null } };

    /// <summary>
    /// No work folder this machine may use at all: no room and no slots. Zero slots means the
    /// session never asks for a job, rather than asking and handing each one straight back.
    /// </summary>
    public static SidecarCapabilities Refused(SidecarCapabilities capabilities) =>
        Unavailable(capabilities) with { MaxConcurrency = 0 };
}
