namespace Optimisarr.Core;

/// <summary>
/// The fingerprint Optimisarr writes into every output file's container metadata so a
/// file can prove it was already optimised — independently of any database. The mark
/// travels <em>with the file</em>: move it to another machine, reinstall, or clear the
/// queue history, and the file is still recognised and skipped rather than transcoded a
/// second time. Read back from <c>format.tags</c> by the probe and honoured by the
/// candidate evaluator.
/// </summary>
public static class OptimisationMarker
{
    /// <summary>
    /// The container metadata key, written via ffmpeg <c>-metadata &lt;key&gt;=…</c> and
    /// read back from ffprobe's <c>format.tags</c>. ffprobe may report tag keys in any
    /// case, so lookups are case-insensitive.
    /// </summary>
    public const string MetadataKey = "optimisarr";

    /// <summary>
    /// Prefix for the image marker. Still encoders (libwebp, mjpeg) silently drop ffmpeg's
    /// <c>-metadata</c>, so an image instead carries its marker in the standard EXIF/XMP
    /// <c>Software</c> field, written and read with exiftool as <c>optimisarr/&lt;value&gt;</c>.
    /// Using <c>Software</c> is honest — Optimisarr is the software that produced the file — and
    /// needs no custom metadata namespace.
    /// </summary>
    public const string ImageSoftwarePrefix = "optimisarr/";

    /// <summary>The <c>Software</c> value to stamp on an optimised image carrying <paramref name="markerValue"/>.</summary>
    public static string FormatImageSoftware(string markerValue) => ImageSoftwarePrefix + markerValue;

    /// <summary>
    /// Reads the marker back out of an image's <c>Software</c> field. Returns the marker value
    /// when the field was written by Optimisarr, otherwise <c>null</c> (so a foreign "Software"
    /// such as "Adobe Photoshop" is correctly treated as not-optimised). Case-insensitive.
    /// </summary>
    public static string? TryParseImageSoftware(string? software)
    {
        if (string.IsNullOrWhiteSpace(software))
        {
            return null;
        }

        var trimmed = software.Trim();
        return trimmed.StartsWith(ImageSoftwarePrefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[ImageSoftwarePrefix.Length..]
            : null;
    }
}
