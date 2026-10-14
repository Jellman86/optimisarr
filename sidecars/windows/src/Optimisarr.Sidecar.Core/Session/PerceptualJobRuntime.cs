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

    internal sealed record Resources(string Volume, long? Disk, long? Memory, bool UsesMemory);

    internal static Resources? ReadResources(string root, Func<string, long?>? freeDisk = null, Func<long?>? freeMemory = null)
    {
        try
        {
            var full = PhysicalRoot(root);
            var drive = DriveInfo.GetDrives().Where(d => full == d.Name.TrimEnd(Path.DirectorySeparatorChar)
                || full.StartsWith(d.Name.EndsWith(Path.DirectorySeparatorChar) ? d.Name : d.Name + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                .MaxBy(d => d.Name.Length);
            if (drive is null) return null;
            return new(drive.Name, freeDisk is null ? drive.AvailableFreeSpace : freeDisk(full),
                freeMemory is null ? NativeMemoryAvailability.Read() : freeMemory(),
                drive.DriveType == DriveType.Ram || drive.DriveFormat is "tmpfs" or "ramfs");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or OverflowException)
        { return null; }
    }

    internal static string PhysicalRoot(string root) => PhysicalRoot(root, 0);

    private static string PhysicalRoot(string root, int depth)
    {
        if (depth >= 40) throw new IOException("The working-space link chain is too deep.");
        var full = Path.GetFullPath(root);
        var physical = Path.GetPathRoot(full)!;
        foreach (var component in full[physical.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            physical = Path.Combine(physical, component);
            var directory = new DirectoryInfo(physical);
            if (directory.LinkTarget is not null)
            {
                // Resolve every ancestor: the workspace itself often does not exist yet, and
                // following only its leaf would hide an alias into a memory-backed volume.
                var target = directory.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not DirectoryInfo resolved || !resolved.Exists)
                    throw new IOException("The working-space link has no accessible directory target.");
                physical = PhysicalRoot(resolved.FullName, depth + 1);
            }
        }
        return physical;
    }

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
