namespace Optimisarr.Core.Workers;

/// <summary>
/// How a worker can score VMAF. Ordered by strength: a machine that proves the CUDA backend can
/// always also score on the CPU, so a stronger capability satisfies a weaker requirement.
/// Apple GPUs have no VMAF compute backend, so a macOS sidecar advertises <see cref="Cpu"/>.
/// </summary>
public enum VmafCapability
{
    None = 0,
    Cpu = 1,
    Cuda = 2
}

/// <summary>
/// What a sidecar has proved it can do. These are discovered by probing the worker's own tools,
/// never assumed from its platform — an advertised capability the bundled build cannot actually
/// run would send jobs somewhere they fail.
/// </summary>
public sealed record WorkerCapabilities(
    string OperatingSystem,
    string Architecture,
    IReadOnlyList<string> VideoEncoders,
    IReadOnlyList<string> HardwareDecoders,
    VmafCapability Vmaf,
    long FreeScratchBytes,
    int MaxConcurrency);

/// <summary>
/// The fully resolved demands of one assignment. The control plane resolves these from the
/// library profile before offering work, so a worker never re-derives policy for itself.
/// </summary>
public sealed record JobRequirements(
    string VideoEncoder,
    string? HardwareDecoder,
    VmafCapability Vmaf,
    long ScratchBytes);

/// <summary>The decision, with every unmet requirement named rather than only the first.</summary>
public sealed record CapabilityMatch(bool Accepted, IReadOnlyList<string> Reasons);

/// <summary>
/// Decides whether an assignment may be offered to a worker at all. Fails closed: an unproved
/// capability is an unmet one, and every rejection carries a reason so an operator can see why a
/// paired sidecar is sitting idle.
/// </summary>
public static class WorkerCapabilityMatcher
{
    public static CapabilityMatch Match(WorkerCapabilities worker, JobRequirements required)
    {
        var reasons = new List<string>();

        if (string.IsNullOrWhiteSpace(required.VideoEncoder))
        {
            // An unnamed encoder is a malformed assignment, not a wildcard.
            reasons.Add("The assignment named no video encoder.");
        }
        else if (!Advertises(worker.VideoEncoders, required.VideoEncoder))
        {
            reasons.Add($"The worker does not advertise the video encoder '{required.VideoEncoder}'.");
        }

        if (!string.IsNullOrWhiteSpace(required.HardwareDecoder)
            && !Advertises(worker.HardwareDecoders, required.HardwareDecoder))
        {
            reasons.Add($"The worker does not advertise the hardware decoder '{required.HardwareDecoder}'.");
        }

        if (worker.Vmaf < required.Vmaf)
        {
            reasons.Add($"The worker's VMAF support ({worker.Vmaf}) is weaker than the required {required.Vmaf}.");
        }

        if (worker.FreeScratchBytes < required.ScratchBytes)
        {
            reasons.Add(
                $"The worker has {worker.FreeScratchBytes} bytes of free scratch space, " +
                $"below the {required.ScratchBytes} this job needs.");
        }

        if (worker.MaxConcurrency <= 0)
        {
            reasons.Add("The worker accepts no concurrent work (drained or disabled).");
        }

        return new CapabilityMatch(reasons.Count == 0, reasons);
    }

    private static bool Advertises(IReadOnlyList<string> advertised, string wanted) =>
        advertised.Any(a => string.Equals(a, wanted, StringComparison.OrdinalIgnoreCase));
}
