using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class ImageMetadataParserTests
{
    [Fact]
    public void Detects_both_an_icc_profile_and_exif()
    {
        const string output = """
            [EXIF]          Make                            : Canon
            [EXIF]          Orientation                     : Horizontal (normal)
            [ICC_Profile]   Profile Description             : sRGB IEC61966-2.1
            """;

        var result = ImageMetadataParser.Parse(output);

        Assert.True(result.HasExif);
        Assert.True(result.HasIccProfile);
    }

    [Fact]
    public void Detects_exif_without_an_icc_profile()
    {
        const string output = "[EXIF]          Orientation                     : Rotate 90 CW";

        var result = ImageMetadataParser.Parse(output);

        Assert.True(result.HasExif);
        Assert.False(result.HasIccProfile);
    }

    [Fact]
    public void Reports_neither_for_a_bare_image()
    {
        var result = ImageMetadataParser.Parse("");

        Assert.False(result.HasExif);
        Assert.False(result.HasIccProfile);
    }
}
