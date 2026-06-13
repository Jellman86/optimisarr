using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class ImageScaleTests
{
    [Fact]
    public void None_produces_no_filter()
    {
        Assert.Null(ImageScale.BuildFilter(ImageDownscaleMode.None, 0));
        Assert.Null(ImageScale.BuildFilter(ImageDownscaleMode.None, 1920));
    }

    [Fact]
    public void Max_long_edge_caps_the_longer_side_and_keeps_aspect()
    {
        var filter = ImageScale.BuildFilter(ImageDownscaleMode.MaxLongEdge, ImageScale.LongEdge1080p);

        Assert.NotNull(filter);
        Assert.Contains("min(iw,1920)", filter);
        Assert.Contains("min(ih,1920)", filter);
        // -2 on the other edge keeps aspect ratio and an even length.
        Assert.Contains("-2", filter);
    }

    [Fact]
    public void A_non_positive_long_edge_is_a_no_op()
    {
        Assert.Null(ImageScale.BuildFilter(ImageDownscaleMode.MaxLongEdge, 0));
    }

    [Fact]
    public void Percent_scales_both_edges_to_even_dimensions()
    {
        var filter = ImageScale.BuildFilter(ImageDownscaleMode.Percent, 50);

        Assert.NotNull(filter);
        Assert.Contains("iw*50/200", filter);
        Assert.Contains("ih*50/200", filter);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(150)]
    public void A_percentage_of_100_or_more_or_zero_never_upscales_and_is_a_no_op(int percent)
    {
        Assert.Null(ImageScale.BuildFilter(ImageDownscaleMode.Percent, percent));
    }
}
