using Optimisarr.Core.Library;

namespace Optimisarr.Tests;

public sealed class SubtitleClassifierTests
{
    [Theory]
    [InlineData("hdmv_pgs_subtitle")]
    [InlineData("dvd_subtitle")]
    [InlineData("dvb_subtitle")]
    [InlineData("xsub")]
    [InlineData("HDMV_PGS_SUBTITLE")] // case-insensitive
    public void Image_based_codecs_are_detected(string codec)
    {
        Assert.True(SubtitleClassifier.IsImageBased(codec));
    }

    [Theory]
    [InlineData("subrip")]
    [InlineData("ass")]
    [InlineData("mov_text")]
    [InlineData("webvtt")]
    [InlineData("")]
    [InlineData(null)]
    public void Text_or_unknown_codecs_are_not_image_based(string? codec)
    {
        Assert.False(SubtitleClassifier.IsImageBased(codec));
    }
}
