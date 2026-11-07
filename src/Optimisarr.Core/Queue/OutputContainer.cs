namespace Optimisarr.Core.Queue;

/// <summary>
/// The container an encode is written into. A library can ask to keep each file's own container, so
/// an optimised file keeps its name: a changed extension is a new path to Sonarr and Radarr, which
/// can unmonitor the title and re-label its quality until they rescan.
/// </summary>
public static class OutputContainer
{
    /// <summary>The library setting that means "whatever the source already is".</summary>
    public const string SameAsSource = "source";

    /// <summary>
    /// The concrete container for this source. Matroska and MP4 are kept. Anything else, such as AVI
    /// or MPEG-TS, cannot reliably hold the target codecs and becomes Matroska, which holds every
    /// stream type. M4V becomes MP4 because FFmpeg's M4V muxer refuses HEVC, and MOV becomes MP4
    /// because MOV cannot hold AV1. A kept extension keeps its spelling, so <c>Film.MKV</c> stays
    /// <c>Film.MKV</c> on a case-sensitive disk.
    /// </summary>
    public static string? For(string? targetContainer, string sourcePath)
    {
        if (!string.Equals(targetContainer, SameAsSource, StringComparison.OrdinalIgnoreCase)) return targetContainer;
        var extension = Path.GetExtension(sourcePath).TrimStart('.');
        return extension.ToLowerInvariant() switch
        {
            "mkv" or "mp4" => extension,
            "m4v" or "mov" => "mp4",
            _ => "mkv",
        };
    }

    /// <summary>
    /// The setting left after a library switches to a preset. A chosen container is a preset override
    /// and is cleared; keeping file names is a choice about paths, so it stays.
    /// </summary>
    public static string? AfterPresetChange(string? targetContainer) =>
        string.Equals(targetContainer, SameAsSource, StringComparison.OrdinalIgnoreCase) ? targetContainer : null;
}
