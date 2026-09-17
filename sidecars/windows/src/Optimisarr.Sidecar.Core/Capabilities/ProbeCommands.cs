namespace Optimisarr.Sidecar.Core.Capabilities;

/// <summary>
/// The throwaway commands that prove a capability rather than assuming it.
/// </summary>
public static class ProbeCommands
{
    /// <summary>
    /// A few frames of a synthetic source to the null muxer. Large enough to clear encoder
    /// minimums — NVENC refuses very small dimensions — rather than a thumbnail.
    /// </summary>
    public static IReadOnlyList<string> VideoEncoder(string encoder) =>
    [
        "-hide_banner", "-v", "error",
        "-f", "lavfi", "-i", "color=c=black:s=320x240:r=25:d=0.2",
        "-frames:v", "3",
        "-c:v", encoder,
        "-f", "null", "-",
    ];

    /// <summary>A fifth of a second of silence: an audio encoder that cannot open fails at once.</summary>
    public static IReadOnlyList<string> AudioEncoder(string encoder) =>
    [
        "-hide_banner", "-v", "error",
        "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo:d=0.2",
        "-c:a", encoder,
        "-f", "null", "-",
    ];

    /// <summary>
    /// Encodes a throwaway clip with a proved encoder, so there is something real to decode back.
    /// </summary>
    public static IReadOnlyList<string> HardwareDecodeEncodeStep(string path, string encoder) =>
    [
        "-hide_banner", "-v", "error", "-y",
        "-f", "lavfi", "-i", "testsrc=s=320x240:r=25:d=1",
        "-c:v", encoder,
        path,
    ];

    /// <summary>
    /// Decodes it back with the accelerator engaged. Listing an accelerator under
    /// <c>-hwaccels</c> only says FFmpeg was compiled for it; both halves have to succeed.
    /// </summary>
    public static IReadOnlyList<string> HardwareDecodeDecodeStep(string path, string accelerator) =>
    [
        "-hide_banner", "-v", "error",
        "-hwaccel", accelerator,
        "-i", path,
        "-f", "null", "-",
    ];

    /// <summary>
    /// Scores two copies of the same clip on the GPU. This is the one capability that cannot be
    /// inferred: a build can carry <c>libvmaf_cuda</c> while the machine has no usable CUDA device,
    /// and Optimisarr will hand a worker a CUDA measurement command only if it says it can run one.
    /// </summary>
    public static IReadOnlyList<string> CudaVmaf(string path) =>
    [
        "-hide_banner", "-v", "error",
        "-hwaccel", "cuda", "-hwaccel_output_format", "cuda", "-i", path,
        "-hwaccel", "cuda", "-hwaccel_output_format", "cuda", "-i", path,
        "-lavfi", "[0:v][1:v]libvmaf_cuda=n_threads=1:n_subsample=5",
        "-f", "null", "-",
    ];

    /// <summary>The same, on the CPU, for a machine with no usable CUDA device.</summary>
    public static IReadOnlyList<string> CpuVmaf(string path) =>
    [
        "-hide_banner", "-v", "error",
        "-i", path, "-i", path,
        "-lavfi", "[0:v][1:v]libvmaf=n_threads=1:n_subsample=5",
        "-f", "null", "-",
    ];
}
