using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Sidecar.Core.Session;

public sealed record ImagePreparationResult(bool MetadataCopied, bool MarkerWritten);

public interface IPerceptualJobTools
{
    Task<RemotePerceptualProbeReport> MeasureAsync(RemotePerceptualSearchStep step, string source, string sourceHash, string scratchRoot, CancellationToken token);
    Task<ImagePreparationResult> PrepareImageAsync(string source, string candidate, string? marker, CancellationToken token);
    Task<RemoteVerificationEvidence> VerifyAsync(string source, string candidate, RemoteVerificationContract contract, string sourceHash, string candidateHash, CancellationToken token);
}

/// <summary>Native tools and peak reservations are held for the entire quality-first job.</summary>
public sealed class PerceptualJobRuntime(IPerceptualJobTools tools, Func<string, long?>? freeDisk = null,
    Func<long?>? freeMemory = null, PerceptualJobResourcePool? pool = null)
{
    public IPerceptualJobTools Tools { get; } = tools;
    // Until a bounded native header-only inspection exists, inventory dimensions cannot safely
    // lower this: the transferred original may differ and self-assessment precedes its probe.
    public const long ImageMemoryBytes = PerceptualResourceBudget.ImageMemoryBytes;
    public const long AudioMemoryBytes = PerceptualResourceBudget.AudioMemoryBytes;

    public IDisposable? Reserve(Assignment assignment, string root)
    {
        if (assignment.RequiredScratchBytes is not { } disk) return null;
        var resources = ReadResources(root, freeDisk, freeMemory);
        if (resources is null || PerceptualResourceBudget.MemoryBytes(assignment.Kind, disk, resources.UsesMemory) is not { } memory) return null;
        return (pool ?? PerceptualJobResourcePool.Shared).TryAcquire(resources.Volume, disk, memory, resources.Disk, resources.Memory);
    }

    internal static NativeWorkingResources? ReadResources(string root, Func<string, long?>? freeDisk = null, Func<long?>? freeMemory = null) =>
        NativeWorkingVolume.Read(root, freeMemory ?? NativeMemoryAvailability.Read, freeDisk);

    internal static string PhysicalRoot(string root) => NativeWorkingVolume.PhysicalRoot(root);

    public static PerceptualJobRuntime Native(string ffmpeg, string? measurementFfmpeg = null) => new(new NativeTools(ffmpeg, measurementFfmpeg));

    private sealed class NativeTools(string ffmpeg, string? measurementFfmpeg) : IPerceptualJobTools
    {
        private readonly PerceptualNativeTools _tools = ResolveNativeTools(ffmpeg);
        public Task<RemotePerceptualProbeReport> MeasureAsync(RemotePerceptualSearchStep step, string source, string sourceHash, string scratchRoot, CancellationToken token) =>
            new NativePerceptualProbeService(_tools).MeasureAsync(step, source, sourceHash, scratchRoot, token);
        public async Task<ImagePreparationResult> PrepareImageAsync(string source, string candidate, string? marker, CancellationToken token)
        {
            var metadata = new ImageMarkerService(_tools.ExifTool);
            var copied = await metadata.CopyMetadataAsync(source, candidate, token);
            var written = !string.IsNullOrWhiteSpace(marker) && await metadata.WriteAsync(candidate, marker, token);
            return new(copied, written);
        }
        public Task<RemoteVerificationEvidence> VerifyAsync(string source, string candidate, RemoteVerificationContract contract,
            string sourceHash, string candidateHash, CancellationToken token) =>
            FullVerification.MeasureAsync(ffmpeg, source, candidate, contract, sourceHash, candidateHash, token, measurementFfmpeg);
    }

    internal static PerceptualNativeTools ResolveNativeTools(string path)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string Tool(string name) => Path.Combine(root, name + (OperatingSystem.IsWindows() ? ".exe" : ""));
        return new(path, Tool("ffprobe"), Environment.GetEnvironmentVariable("OPTIMISARR_AUDIO_QUALITY") ?? Tool("optimisarr-audio-quality"),
            Environment.GetEnvironmentVariable("OPTIMISARR_IMAGE_QUALITY") ?? Tool("optimisarr-image-quality"),
            NativeToolLocation.Resolve("exiftool", root, Environment.GetEnvironmentVariable("OPTIMISARR_EXIFTOOL"), Environment.GetEnvironmentVariable("PATH")));
    }
}
