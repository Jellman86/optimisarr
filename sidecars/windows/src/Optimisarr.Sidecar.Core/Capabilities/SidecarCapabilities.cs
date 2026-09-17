namespace Optimisarr.Sidecar.Core.Capabilities;

/// <summary>How well this machine can measure VMAF. Mirrors the server's own enum.</summary>
public enum VmafCapability
{
    None = 0,
    Cpu = 1,
    Cuda = 2,
}

/// <summary>
/// What this machine proved it can do. Every list here was earned by running something, never by
/// reading a listing: the server schedules against these and a capability it believes but the
/// machine cannot honour is a job that can only fail.
/// </summary>
public sealed record SidecarCapabilities(
    string Name,
    string OperatingSystem,
    string Architecture,
    IReadOnlyList<string> VideoEncoders,
    IReadOnlyList<string> AudioEncoders,
    IReadOnlyList<string> HardwareDecoders,
    VmafCapability Vmaf,
    long FreeScratchBytes,
    int MaxConcurrency)
{
    /// <summary>What a machine with no FFmpeg reports: nothing, and no capacity to take work.</summary>
    public static SidecarCapabilities Nothing(string name) => new(
        name,
        OperatingSystem: "windows",
        Architecture: System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        VideoEncoders: [],
        AudioEncoders: [],
        HardwareDecoders: [],
        Vmaf: VmafCapability.None,
        FreeScratchBytes: 0,
        MaxConcurrency: 0);
}
