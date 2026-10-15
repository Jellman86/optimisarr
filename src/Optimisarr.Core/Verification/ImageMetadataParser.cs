using System.Text.Json;

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
    /// <summary>Grouped JSON keeps arbitrary metadata values separate from evidence keys.</summary>
    public static ImageMetadataResult ParseJson(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 1 || root[0].ValueKind != JsonValueKind.Object
                || !root[0].TryGetProperty("SourceFile", out var source) || source.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(source.GetString()))
                return ImageMetadataResult.Failed("ExifTool returned incomplete metadata evidence.");
            var icc = false; var exif = false;
            foreach (var item in root[0].EnumerateObject())
            {
                if (item.Name is "ExifTool:Warning" or "ExifTool:Error")
                    return ImageMetadataResult.Failed("ExifTool could not fully inspect the metadata: " + item.Value.ToString());
                if (item.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
                icc |= item.Name.StartsWith("ICC_Profile:", StringComparison.Ordinal) || item.Name.EndsWith(":ICC_Profile", StringComparison.Ordinal);
                exif |= item.Name.StartsWith("EXIF:", StringComparison.Ordinal);
            }
            return ImageMetadataResult.Ok(new(icc, exif));
        }
        catch (JsonException) { return ImageMetadataResult.Failed("ExifTool returned malformed metadata evidence."); }
    }

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
