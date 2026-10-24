namespace Optimisarr.Core.Library;

/// <summary>Shell-free exiftool arguments for copying replacement-critical image metadata.</summary>
public static class ImageMetadataCommandBuilder
{
    public static IReadOnlyList<string> BuildCopyArguments(string sourcePath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        return
        [
            "-overwrite_original",
            "-TagsFromFile", sourcePath,
            "-EXIF:all",
            // ICC's individual fields are read-only. Copy the complete binary profile;
            // ICC_Profile:all can exit successfully without writing any profile at all.
            "-ICC_Profile",
            // FFmpeg emits display-ready pixels. Re-copying the source orientation would make
            // viewers rotate them a second time; embedded previews and dimension tags likewise
            // describe the old raster and must not be transplanted.
            "--Orientation",
            "--ThumbnailImage",
            "--PreviewImage",
            "--JpgFromRaw",
            "--ImageWidth",
            "--ImageHeight",
            "--ExifImageWidth",
            "--ExifImageHeight",
            outputPath
        ];
    }
}
