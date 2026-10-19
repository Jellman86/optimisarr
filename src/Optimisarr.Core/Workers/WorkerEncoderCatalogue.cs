using Optimisarr.Core.Queue;
using Optimisarr.Core.Tools;

namespace Optimisarr.Core.Workers;

/// <summary>
/// Describes a sidecar's advertised encoder names in the vocabulary <see cref="Queue.EncoderSelector"/>
/// understands, so the control plane can choose an encoder for a worker exactly the way it chooses
/// one for itself. Fails closed: a name this has not been taught is dropped, because no assignment
/// could carry arguments for an encoder the command builder does not know.
/// </summary>
public static class WorkerEncoderCatalogue
{
    public static IReadOnlyList<EncoderCapability> Describe(IEnumerable<string> advertised)
    {
        var described = new List<EncoderCapability>();
        foreach (var raw in advertised)
        {
            var name = raw.Trim().ToLowerInvariant();
            if (name.Length == 0 || described.Any(existing => existing.Name == name))
            {
                continue;
            }

            if (Family(name) is { } mode && Codec(name) is { } codec)
            {
                described.Add(new EncoderCapability(name, codec, mode, Available: true));
            }
        }

        return described;
    }

    /// <summary>
    /// The encoder mode a worker is chosen under. CPU mode is a software-encoding policy and
    /// binds every machine (#425); a vendor mode names this server's GPU family, so a worker is
    /// instead given the best hardware it proved, exactly as Auto chooses here.
    /// </summary>
    public static EncoderMode ModeForWorker(EncoderMode queueMode) =>
        queueMode == EncoderMode.Cpu ? EncoderMode.Cpu : EncoderMode.Auto;

    /// <summary>Chooses a worker's encoder from the names it advertised, under the queue's mode.</summary>
    public static EncoderSelection Select(
        string targetCodec,
        EncoderMode queueMode,
        IEnumerable<string> advertised,
        int? sourceBitDepth = null) =>
        EncoderSelector.Select(targetCodec, ModeForWorker(queueMode), Describe(advertised), sourceBitDepth);

    // The mode strings are the ones the local hardware probe reports, so the selector's preference
    // order applies unchanged to a remote worker.
    private static string? Family(string name) =>
        name is "libx264" or "libx265" or "libsvtav1" ? "CPU"
        : name.EndsWith("_nvenc", StringComparison.Ordinal) ? "NVIDIA NVENC"
        : name.EndsWith("_qsv", StringComparison.Ordinal) ? "Intel QSV"
        : name.EndsWith("_vaapi", StringComparison.Ordinal) ? "VAAPI"
        : name.EndsWith("_videotoolbox", StringComparison.Ordinal) ? "VideoToolbox"
        : null;

    private static string? Codec(string name) => name switch
    {
        "libx264" => "h264",
        "libx265" => "hevc",
        "libsvtav1" => "av1",
        _ when name.StartsWith("h264_", StringComparison.Ordinal) => "h264",
        _ when name.StartsWith("hevc_", StringComparison.Ordinal) => "hevc",
        _ when name.StartsWith("av1_", StringComparison.Ordinal) => "av1",
        _ => null
    };
}
