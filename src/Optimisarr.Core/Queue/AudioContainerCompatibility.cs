namespace Optimisarr.Core.Queue;

/// <summary>
/// Plans preserving containers for copied audio: unmuxable Blu-ray formats and Matroska ALAC
/// whose MP4 edit lists can hide tail samples. Pure and unit tested.
/// </summary>
public static class AudioContainerCompatibility
{
    public const string AlacRemuxNoChangeReason =
        "MP4 could omit the copied ALAC tail; preserving Matroska leaves no remaining remux changes";
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

    /// <summary>
    /// Matroska ALAC packets can lack the final duration expected by MP4 edit lists. Copying them
    /// can hide the last partial packet during normal playback even though muxing succeeds.
    /// Keep the source's preserving container; do not disable edit lists for other streams.
    /// </summary>
    public static bool CopiedAlacNeedsMatroska(string? sourceExtension,
        IReadOnlyList<string>? sourceAudioCodecs, IReadOnlyCollection<int> removedAudioIndexes) =>
        sourceExtension?.TrimStart('.').ToLowerInvariant() is "mkv" or "mka"
        && sourceAudioCodecs is not null
        && sourceAudioCodecs.Where((_, index) => !removedAudioIndexes.Contains(index))
            .Any(codec => string.Equals(codec, "alac", StringComparison.OrdinalIgnoreCase));

    public static bool CopiedAlacFallbackHasNoWork(TranscodeSpec spec, IReadOnlyList<string>? sourceAudioCodecs)
        => CopiedAlacNeedsMatroska(Path.GetExtension(spec.InputPath), sourceAudioCodecs,
                spec.RemoveAudioStreamIndexes ?? [])
            && spec.VideoCodec is null && spec.AudioEncoder is null
            && (spec.RemoveAudioStreamIndexes?.Count ?? 0) == 0
            && (spec.RemoveSubtitleStreamIndexes?.Count ?? 0) == 0
            && string.Equals(Path.GetExtension(spec.OutputPath), ".mkv", StringComparison.OrdinalIgnoreCase);
}
