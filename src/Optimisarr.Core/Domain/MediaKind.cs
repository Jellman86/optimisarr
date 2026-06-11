namespace Optimisarr.Core.Domain;

/// <summary>
/// What a single file actually is, detected from its probe and extension — distinct from
/// a library's configured <see cref="MediaType"/>. Drives which optimisation pipeline a
/// file belongs to (video, audio, or image).
/// </summary>
public enum MediaKind
{
    /// <summary>Not recognised as video, audio, or image (e.g. a stray subtitle or data file).</summary>
    Unknown = 0,
    Video = 1,
    Audio = 2,
    Image = 3
}

/// <summary>
/// Pure classification of a file into a <see cref="MediaKind"/> from facts the probe can
/// gather. Kept deterministic and free of I/O so it is fully unit tested.
/// </summary>
public static class MediaKindClassifier
{
    // Still-image containers report a video stream (mjpeg, png, …), so the extension is the
    // most reliable signal that a file is a picture rather than a one-frame video.
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff",
        ".heic", ".heif", ".avif", ".jxl"
    };

    /// <param name="extension">The file extension, with or without a leading dot.</param>
    /// <param name="hasNonCoverVideoStream">A video stream that is not embedded cover art (attached picture).</param>
    /// <param name="hasAudioStream">At least one audio stream is present.</param>
    public static MediaKind Classify(string? extension, bool hasNonCoverVideoStream, bool hasAudioStream)
    {
        if (extension is not null && ImageExtensions.Contains(Normalise(extension)))
        {
            return MediaKind.Image;
        }

        if (hasNonCoverVideoStream)
        {
            return MediaKind.Video;
        }

        return hasAudioStream ? MediaKind.Audio : MediaKind.Unknown;
    }

    private static string Normalise(string extension) =>
        extension.StartsWith('.') ? extension : "." + extension;
}
