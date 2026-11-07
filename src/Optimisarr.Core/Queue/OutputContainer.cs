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
    /// stream type; M4V and MOV become MP4, because FFmpeg's M4V muxer refuses HEVC.
    /// </summary>
    public static string? For(string? targetContainer, string sourcePath)
    {
        if (!string.Equals(targetContainer, SameAsSource, StringComparison.OrdinalIgnoreCase)) return targetContainer;
        return Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant() switch
        {
            "mkv" => "mkv",
            "mp4" or "m4v" or "mov" => "mp4",
            _ => "mkv",
        };
    }
}
