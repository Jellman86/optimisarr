using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Queue;

/// <summary>Chooses when source decoding may safely use the selected hardware decoder.</summary>
public static class HardwareDecodePolicy
{
    /// <summary>
    /// Keeps short interactive video comparisons on software decode. Some hardware decoders expose
    /// reordered frames before an input seek (notably QSV with long-GOP H.264), so the encoded clip
    /// can begin several frames before the requested source window and produce a meaningless VMAF
    /// comparison. These clips are deliberately short; preserving exact frame identity is more
    /// important than accelerating their decode, while the selected hardware encoder remains in use.
    /// </summary>
    public static bool ShouldUse(
        bool configured,
        bool isDisposable,
        MediaKind kind,
        string? videoCodec,
        int? clipSeconds,
        // A software picture filter — the downscale — cannot consume frames that a hardware
        // decoder left on the GPU; ffmpeg fails the graph outright. Decoding in software keeps the
        // filter working while the selected hardware encoder stays in use.
        bool requiresSoftwareFilter = false) =>
        configured
        && !requiresSoftwareFilter
        && !(isDisposable
            && kind is not (MediaKind.Audio or MediaKind.Image)
            && videoCodec is not null
            && clipSeconds is > 0);
}
