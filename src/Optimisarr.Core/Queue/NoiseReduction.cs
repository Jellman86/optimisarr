namespace Optimisarr.Core.Queue;

/// <summary>How strongly a library's video re-encodes reduce noise, such as the grain of a tape capture.</summary>
public enum NoiseReduction
{
    Off = 0,
    Light = 1,
    Strong = 2,
}

/// <summary>
/// The FFmpeg filter for each noise reduction level. <c>hqdn3d</c> is fast on the CPU and deterministic, so the
/// VMAF reference can be cleaned exactly as the encode was, and the gate then judges how faithfully the encode
/// kept the cleaned picture. Chroma is cleaned harder than luma, because tape carries little colour detail and most
/// of its visible noise is colour noise, while smoothing luma is what makes faces look waxy. Temporal strength stays
/// moderate, since more leaves trails behind movement. The levels are names, so the filter behind them can change
/// later without a migration.
/// </summary>
public static class NoiseReductionFilter
{
    public static string? For(NoiseReduction level) => level switch
    {
        NoiseReduction.Light => "hqdn3d=luma_spatial=1.5:chroma_spatial=3:luma_tmp=3:chroma_tmp=4.5",
        NoiseReduction.Strong => "hqdn3d=luma_spatial=3:chroma_spatial=5:luma_tmp=6:chroma_tmp=7",
        _ => null,
    };

    /// <summary>
    /// Whether a job of this kind reduces noise, which keeps it on this server: a worker's quality contract has
    /// no way to clean its reference, so it would judge a cleaned candidate against the noisy original.
    /// </summary>
    public static bool KeepsOnServer(NoiseReduction level, Domain.MediaKind kind) =>
        level != NoiseReduction.Off && kind == Domain.MediaKind.Video;
}
