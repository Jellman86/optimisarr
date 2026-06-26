namespace Optimisarr.Core.Library;

/// <summary>
/// Pure ffmpeg argument builders and image sniffing for the small thumbnail a list row shows. The
/// arguments are explicit arrays (the source path is always a discrete argument, never interpolated
/// into a shell string) so the API layer can run them safely; the bytes ffmpeg writes to stdout are
/// served as-is. No process is started here, so this is unit tested without ffmpeg.
/// </summary>
public static class MediaThumbnail
{
    /// <summary>
    /// Extracts an audio file's embedded cover art to stdout. <c>0:v</c> minus <c>0:V</c> selects the
    /// attached-picture stream(s) while excluding any real video, so this only ever copies the cover.
    /// Exits non-zero when the file has no embedded picture, which the caller treats as "no thumbnail".
    /// </summary>
    public static IReadOnlyList<string> CoverArtArguments(string sourcePath) =>
    [
        "-hide_banner", "-loglevel", "error",
        "-i", sourcePath,
        "-map", "0:v", "-map", "-0:V",
        "-frames:v", "1",
        "-c", "copy",
        "-f", "image2pipe", "pipe:1"
    ];

    /// <summary>
    /// Renders a single down-scaled JPEG thumbnail of an image file to stdout, scaled to
    /// <paramref name="height"/> pixels tall with an even, aspect-preserving width. One frame, so an
    /// animated source yields its first frame rather than a stream.
    /// </summary>
    public static IReadOnlyList<string> ImageThumbnailArguments(string sourcePath, int height) =>
    [
        "-hide_banner", "-loglevel", "error",
        "-i", sourcePath,
        "-frames:v", "1",
        "-vf", $"scale=-2:{height}",
        "-q:v", "5",
        "-f", "image2pipe", "-vcodec", "mjpeg", "pipe:1"
    ];

    /// <summary>
    /// The image content type for a byte buffer, sniffed from its magic number, or null when the
    /// buffer is not a recognised image (so a stray, non-image ffmpeg output is never served).
    /// </summary>
    public static string? DetectImageContentType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }
        if (bytes.Length >= 6 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F')
        {
            return "image/gif";
        }
        if (bytes.Length >= 12
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            return "image/webp";
        }

        return null;
    }
}
