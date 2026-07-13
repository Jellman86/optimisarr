using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class ImageQualityCommandBuilderTests
{
    [Fact]
    public void Measurement_uses_explicit_dimensions_timebase_full_range_and_rgb()
    {
        var command = ImageQualityCommandBuilder.Build(
            "/work/output.jpg",
            "/data/original.png",
            "/tmp/ssim.log",
            new ImageQualityMeasurementContext(4000, 3000, ReferenceMayHaveAlpha: false));

        Assert.Contains(
            "[0:v]settb=AVTB,setpts=PTS-STARTPTS,scale=4000:3000:flags=bicubic:in_range=auto:out_range=full,format=gbrp[dist]",
            command.FilterGraph);
        Assert.Contains(
            "[1:v]settb=AVTB,setpts=PTS-STARTPTS,scale=4000:3000:flags=bicubic:in_range=auto:out_range=full,format=gbrp[ref]",
            command.FilterGraph);
        Assert.Contains("shortest=1:repeatlast=0", command.FilterGraph);
        Assert.DoesNotContain("scale2ref", command.FilterGraph);
    }

    [Fact]
    public void Alpha_capable_reference_compares_rgba_planes()
    {
        var command = ImageQualityCommandBuilder.Build(
            "output.webp", "original.png", "/tmp/ssim.log",
            new ImageQualityMeasurementContext(800, 600, ReferenceMayHaveAlpha: true));

        Assert.Equal(2, command.FilterGraph.Split("format=gbrap").Length - 1);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    public void Missing_reference_dimensions_are_rejected(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageQualityCommandBuilder.Build(
            "output.webp", "original.png", "/tmp/ssim.log",
            new ImageQualityMeasurementContext(width, height, ReferenceMayHaveAlpha: false)));
    }
}
