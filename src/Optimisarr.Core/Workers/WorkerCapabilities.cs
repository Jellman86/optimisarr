using Optimisarr.Core.Domain;
using Optimisarr.Core.Verification;

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
    IReadOnlyList<string> AudioEncoders,
    IReadOnlyList<string> HardwareDecoders,
    VmafCapability Vmaf,
    long FreeScratchBytes,
    int MaxConcurrency,
    string? SidecarVersion = null,
    int ProtocolVersion = 1,
    WorkerPerceptualCapabilities? Perceptual = null);

/// <summary>Native tool and still-encoder proof collected on the worker, never inferred from its platform.</summary>
public sealed record WorkerPerceptualCapabilities(IReadOnlyList<string>? ImageEncoders = null,
    bool ImageMetadataSupported = false, string? AudioMetricRevision = null, string? ImageMetricRevision = null,
    long? AvailableMemoryBytes = null, bool? ScratchUsesMemory = null)
{
    public bool Valid() => (ImageEncoders is null || ImageEncoders.Count <= 64
        && ImageEncoders.All(name => name is { Length: > 0 and <= 64 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        && ImageEncoders.Sum(name => name.Length + 1) <= 1024)
        && Revision(AudioMetricRevision) && Revision(ImageMetricRevision) && AvailableMemoryBytes is not < 0;

    private static bool Revision(string? revision) => revision is null || revision is { Length: 40 } && revision.All(Uri.IsHexDigit);
}

/// <summary>
/// The fully resolved demands of one assignment. The control plane resolves these from the
/// library profile before offering work, so a worker never re-derives policy for itself.
/// </summary>
public sealed record JobRequirements(
    string? VideoEncoder,
    string? AudioEncoder,
    string? HardwareDecoder,
    VmafCapability Vmaf,
    long ScratchBytes,
    MediaKind Kind = MediaKind.Video,
    string? ImageEncoder = null,
    bool PerceptualSearch = false);

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

        if (!Enum.IsDefined(required.Kind)) reasons.Add("Unsupported assignment media kind.");
        if (required.PerceptualSearch)
        {
            var memory = PerceptualResourceBudget.MemoryBytes(required.Kind, required.ScratchBytes, worker.Perceptual?.ScratchUsesMemory);
            if (memory is null || worker.Perceptual?.AvailableMemoryBytes is not { } available)
                reasons.Add("The worker could not confirm its available native memory and RAM working-space budget.");
            else if (available < memory)
                reasons.Add($"The worker has {available} bytes of available native memory, below the {memory} this job needs including any RAM working space.");
        }
        if (required.PerceptualSearch && worker.ProtocolVersion < WorkerProtocol.PerceptualQualityVersion)
            reasons.Add($"Audio/image quality-first requires sidecar protocol {WorkerProtocol.PerceptualQualityVersion}.");
        if (required.PerceptualSearch && required.Kind != MediaKind.Image
            && worker.Perceptual?.AudioMetricRevision != AudioQualityResultParser.Revision)
            reasons.Add("Quality-first requires the worker's proved, pinned native audio metric.");

        if (required.Kind == MediaKind.Image)
        {
            if (worker.ProtocolVersion < WorkerProtocol.PerceptualQualityVersion)
                reasons.Add($"Still-image encoding requires sidecar protocol {WorkerProtocol.PerceptualQualityVersion}.");
            if (string.IsNullOrWhiteSpace(required.ImageEncoder)
                || !Advertises(worker.Perceptual?.ImageEncoders ?? [], required.ImageEncoder))
                reasons.Add($"The worker does not prove the image encoder '{required.ImageEncoder ?? "unspecified"}'.");
            if (worker.Perceptual?.ImageMetadataSupported != true)
                reasons.Add("Still-image encoding requires proved metadata-copy and verification tools.");
            if (required.PerceptualSearch && worker.Perceptual?.ImageMetricRevision != ImagePerceptualResultParser.Revision)
                reasons.Add("Image quality-first requires the worker's proved, pinned native image metric.");
        }
        else if (required.Kind == MediaKind.Audio)
        {
            if (string.IsNullOrWhiteSpace(required.AudioEncoder))
                reasons.Add("The audio assignment named no audio encoder.");
            if (worker.ProtocolVersion < 6)
                reasons.Add("Standalone audio requires sidecar protocol 6.");
        }
        else if (string.IsNullOrWhiteSpace(required.VideoEncoder))
        {
            // An unnamed encoder is a malformed assignment, not a wildcard.
            reasons.Add("The assignment named no video encoder.");
        }
        else if (!Advertises(worker.VideoEncoders, required.VideoEncoder))
        {
            reasons.Add($"The worker does not advertise the video encoder '{required.VideoEncoder}'.");
        }

        // Null means the audio is copied, which needs no encoder. A named one has to be proved,
        // exactly like the video encoder: the alternative is an "Unknown encoder" failure on the
        // worker for a library setting the operator was allowed to choose.
        if (!string.IsNullOrWhiteSpace(required.AudioEncoder)
            && !Advertises(worker.AudioEncoders, required.AudioEncoder))
        {
            reasons.Add($"The worker does not advertise the audio encoder '{required.AudioEncoder}'.");
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
