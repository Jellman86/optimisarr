using Optimisarr.Sidecar.Core.Session;

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
    public static IReadOnlyList<string> VideoEncoder(string encoder, bool linuxDevices = false) =>
    [
        "-hide_banner", "-v", "error",
        .. DeviceArguments(encoder, linuxDevices),
        "-f", "lavfi", "-i", "color=c=black:s=320x240:r=25:d=0.2",
        "-frames:v", "3",
        .. (encoder.EndsWith("_vaapi", StringComparison.Ordinal) ? new[] { "-vf", "format=nv12,hwupload" } : Array.Empty<string>()),
        "-c:v", encoder,
        "-f", "null", "-",
    ];

    private static IReadOnlyList<string> DeviceArguments(string encoder, bool linuxDevices) =>
        !linuxDevices ? [] : encoder.EndsWith("_vaapi", StringComparison.Ordinal)
            ? ["-vaapi_device", "/dev/dri/renderD128"]
            : encoder.EndsWith("_qsv", StringComparison.Ordinal)
                ? ["-init_hw_device", "qsv=hw", "-filter_hw_device", "hw"] : [];

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
        "-pix_fmt", "yuv420p",
        "-c:v", encoder,
        path,
    ];

    /// <summary>
    /// Decodes it back with the accelerator engaged. Listing an accelerator under
    /// <c>-hwaccels</c> only says FFmpeg was compiled for it; both halves have to succeed.
    /// </summary>
    public static IReadOnlyList<string> HardwareDecodeDecodeStep(string path, string accelerator, bool linuxDevices = false) =>
    [
        "-hide_banner", "-v", "error",
        .. DeviceArguments(accelerator == "vaapi" ? "hevc_vaapi" : accelerator == "qsv" ? "hevc_qsv" : "", linuxDevices),
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

    public static IReadOnlyList<string> V1Vmaf(string path, string model, string log) =>
    [
        "-nostdin", "-hide_banner", "-v", "error", "-i", path, "-i", path,
        "-lavfi", $"[0:v]format=yuv420p10le[d];[1:v]format=yuv420p10le[r];[d][r]libvmaf=model='version={model}\\:cambi.enc_width=320\\:cambi.enc_height=240\\:cambi.enc_bitdepth=8':n_threads=1:n_subsample=5:log_fmt=json:log_path={FilterPath.ForFilterOption(log)}:shortest=1:repeatlast=0",
        "-f", "null", "-"
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
