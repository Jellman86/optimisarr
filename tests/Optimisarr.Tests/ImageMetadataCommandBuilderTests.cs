using Optimisarr.Core.Library;

namespace Optimisarr.Tests;

public sealed class ImageMetadataCommandBuilderTests
{
    [Fact]
    public void Copy_command_preserves_exif_and_icc_without_a_shell()
    {
        var arguments = ImageMetadataCommandBuilder.BuildCopyArguments(
            "/data/Photos/a name.png", "/work/Photos/a name.webp");

        Assert.Equal(
            [
                "-overwrite_original",
                "-TagsFromFile", "/data/Photos/a name.png",
                "-EXIF:all",
                "-ICC_Profile:all",
                "--Orientation",
                "--ThumbnailImage",
                "--PreviewImage",
                "--JpgFromRaw",
                "--ImageWidth",
                "--ImageHeight",
                "--ExifImageWidth",
                "--ExifImageHeight",
                "/work/Photos/a name.webp"
            ],
            arguments);
    }
}
