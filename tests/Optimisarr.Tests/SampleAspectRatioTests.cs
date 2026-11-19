using Optimisarr.Core.Domain;

namespace Optimisarr.Tests;

/// <summary>
/// A DVD stores 720x576 pixels that are shown wider or narrower than they are stored. ffprobe
/// reports that shape as a sample aspect ratio, and "0:1" or nothing at all when it is unknown.
/// </summary>
public sealed class SampleAspectRatioTests
{
    [Fact]
    public void Parse_reads_a_pal_widescreen_pixel_shape()
    {
        var ratio = SampleAspectRatio.Parse("64:45");

        Assert.Equal(new SampleAspectRatio(64, 45), ratio);
        Assert.False(ratio!.Value.IsSquare);
        Assert.Equal("64/45", ratio.Value.FilterValue);
        Assert.Equal(64 / 45.0, ratio.Value.Value, 9);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0:1")]
    [InlineData("N/A")]
    public void A_missing_or_unknown_pixel_shape_is_unspecified(string? text) =>
        Assert.Null(SampleAspectRatio.Parse(text));

    [Theory]
    [InlineData("0:45")]
    [InlineData("64:0")]
    [InlineData("-64:45")]
    [InlineData("64")]
    [InlineData("a:b")]
    public void A_zero_negative_or_malformed_term_is_not_a_ratio(string text) =>
        Assert.Null(SampleAspectRatio.Parse(text));

    [Fact]
    public void One_to_one_is_square() => Assert.True(SampleAspectRatio.Parse("1:1")!.Value.IsSquare);

    [Theory]
    [InlineData("64:45", "64:45", true)]
    [InlineData(null, "1:1", true)]
    [InlineData("0:1", null, true)]
    // A downscale can nudge the ratio when the new size rounds; that is not a change of shape.
    [InlineData("1:1", "1280:1281", true)]
    [InlineData("64:45", "1:1", false)]
    [InlineData(null, "64:45", false)]
    // The closest pair of real pixel shapes is about 2% apart and must still count as different.
    [InlineData("10:11", "8:9", false)]
    public void Two_shapes_match_only_within_rounding(string? first, string? second, bool same) =>
        Assert.Equal(same, SampleAspectRatio.SameShape(SampleAspectRatio.Parse(first), SampleAspectRatio.Parse(second)));
}
