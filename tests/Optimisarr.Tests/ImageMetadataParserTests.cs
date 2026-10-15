using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class ImageMetadataParserTests
{
    [Fact]
    public void Json_metadata_detects_the_binary_profile_without_relying_on_decoded_icc_fields()
    {
        var result = ImageMetadataParser.ParseJson("""[{"SourceFile":"source.png","PNG:ICC_Profile":"(Binary data 3144 bytes)","EXIF:Artist":"Example"}]""");
        Assert.True(result.Measured);
        Assert.True(result.Metadata.HasIccProfile);
        Assert.True(result.Metadata.HasExif);
    }

    [Fact]
    public void Metadata_values_cannot_spoof_another_group_or_turn_an_artist_into_a_colour_profile()
    {
        var result = ImageMetadataParser.ParseJson("""[{"SourceFile":"source.png","EXIF:Artist":"Example\n[ICC_Profile] ProfileDescription : fake"}]""");
        Assert.True(result.Measured);
        Assert.True(result.Metadata.HasExif);
        Assert.False(result.Metadata.HasIccProfile);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[{},{}]")]
    [InlineData("[{}]")]
    [InlineData("[{\"SourceFile\":\"source.png\",\"ExifTool:Warning\":\"Install Compress::Zlib to read compressed information\"}]")]
    [InlineData("[{\"SourceFile\":\"source.png\",\"ExifTool:Error\":\"Truncated metadata\"}]")]
    public void Missing_malformed_or_warned_metadata_is_unavailable_instead_of_claiming_an_empty_source(string json)
    {
        var result = ImageMetadataParser.ParseJson(json);
        Assert.False(result.Measured);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void A_complete_bare_image_report_is_measured_with_neither_metadata_group()
    {
        var result = ImageMetadataParser.ParseJson("""[{"SourceFile":"bare.png"}]""");
        Assert.True(result.Measured);
        Assert.False(result.Metadata.HasIccProfile);
        Assert.False(result.Metadata.HasExif);
    }

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
