namespace Optimisarr.Core.Verification;

/// <summary>The metadata an image carries, as detected from exiftool's grouped output.</summary>
public readonly record struct ImageMetadata(bool HasIccProfile, bool HasExif);

/// <summary>
/// Parses exiftool's family-0 grouped output (<c>-G0 -s</c>, restricted to the EXIF and
/// ICC_Profile groups) into a simple presence check. Each line is prefixed with its group,
/// e.g. <c>[EXIF] Orientation : …</c> or <c>[ICC_Profile] Profile Description : …</c>, so the
/// presence of either group means the file carries that metadata. Pure and unit tested.
/// </summary>
public static class ImageMetadataParser
{
    public static ImageMetadata Parse(string exiftoolOutput)
    {
        var hasIcc = false;
        var hasExif = false;

        foreach (var rawLine in exiftoolOutput.Split('\n'))
        {
            var line = rawLine.TrimStart();
            if (line.StartsWith("[ICC_Profile]", StringComparison.Ordinal))
            {
                hasIcc = true;
            }
            else if (line.StartsWith("[EXIF]", StringComparison.Ordinal))
            {
                hasExif = true;
            }
        }

        return new ImageMetadata(hasIcc, hasExif);
    }
}
