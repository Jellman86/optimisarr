namespace Optimisarr.Core.Queue;

/// <summary>
/// Audio codecs an MP4/MOV muxer has no registered tag for. Copying one into an MP4-family container
/// aborts the whole job ("Could not find tag for codec ... in stream"), so when the audio is being
/// copied (not re-encoded) the resolver falls back to Matroska, which holds them. Pure and unit tested.
/// </summary>
public static class AudioContainerCompatibility
{
    // The lossless/bitstream formats Blu-ray carries that MP4 cannot mux: Dolby TrueHD (and its MLP
    // core) and the Blu-ray/DVD LPCM variants. AAC, AC-3, E-AC-3, Opus, FLAC, and DTS all have MP4
    // tags and mux fine, so they are deliberately not listed.
    private static readonly HashSet<string> Mp4Incompatible = new(StringComparer.OrdinalIgnoreCase)
    {
        "truehd",
        "mlp",
        "pcm_bluray",
        "pcm_dvd",
    };

    public static bool IsMp4Incompatible(string? codec) =>
        !string.IsNullOrWhiteSpace(codec) && Mp4Incompatible.Contains(codec.Trim());

    /// <summary>
    /// True when any codec in an inventory audio summary (a comma-joined list such as
    /// <c>"truehd, ac3"</c>) cannot be muxed into an MP4-family container.
    /// </summary>
    public static bool ContainsMp4Incompatible(string? audioCodecs) =>
        !string.IsNullOrWhiteSpace(audioCodecs)
        && audioCodecs.Split(',').Any(IsMp4Incompatible);
}
