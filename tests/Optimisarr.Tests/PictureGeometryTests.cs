using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

/// <summary>
/// One number has to feed both the FFmpeg scale filter and the verification gate, or an off-by-two
/// rounding disagreement fails a good encode. These pin the width FFmpeg's own <c>scale=-2:H</c>
/// chooses — measured against a real ffmpeg, not derived — so Optimisarr picks the same one.
/// </summary>
public sealed class PictureGeometryTests
{
    // Every row below was produced by running scale=-2:H through ffmpeg and reading the output
    // back with ffprobe. The two non-integer cases are the ones that matter: 853.33 -> 854 and
    // 1150.56 -> 1150 both round to the nearest even value, not up, not down, not truncated.
    [Theory]
    [InlineData(1920, 1080, 720, 1280)]
    [InlineData(1920, 1080, 480, 854)]
    [InlineData(1998, 1080, 720, 1332)]
    [InlineData(1998, 1080, 480, 888)]
    [InlineData(3840, 1600, 720, 1728)]
    [InlineData(3840, 1600, 480, 1152)]
    [InlineData(1440, 1080, 720, 960)]
    [InlineData(1440, 1080, 480, 640)]
    [InlineData(1280, 534, 480, 1150)]
    public void The_width_matches_what_ffmpeg_itself_would_choose(
        int sourceWidth, int sourceHeight, int targetHeight, int expectedWidth)
    {
        var size = PictureGeometry.Downscale(sourceWidth, sourceHeight, targetHeight);

        Assert.NotNull(size);
        Assert.Equal(expectedWidth, size.Width);
        Assert.Equal(targetHeight, size.Height);
    }

    [Fact]
    public void The_filter_states_both_dimensions_explicitly()
    {
        // Never "-2". The whole point is that the filter and the gate share a number rather than
        // each rounding for themselves.
        var filter = PictureGeometry.ScaleFilter(new PictureSize(854, 480));

        Assert.StartsWith("scale=854:480", filter);
        Assert.DoesNotContain("-2", filter);
    }

    [Fact]
    public void A_source_already_at_the_target_is_left_alone()
    {
        Assert.Null(PictureGeometry.Downscale(1280, 720, 720));
    }

    [Fact]
    public void A_source_below_the_target_is_never_upscaled()
    {
        // A downscale exists to save space. Upscaling spends bits to invent nothing, and a 4K
        // reference compared against an upscaled 480p source would score badly for no reason.
        Assert.Null(PictureGeometry.Downscale(720, 576, 720));
        Assert.Null(PictureGeometry.Downscale(1280, 534, 720));
    }

    [Fact]
    public void Unknown_source_dimensions_produce_no_transform()
    {
        // An unprobed file has nothing to scale from. Doing nothing is the only honest answer;
        // the structural gate then compares output to source as it always has.
        Assert.Null(PictureGeometry.Downscale(null, 1080, 720));
        Assert.Null(PictureGeometry.Downscale(1920, null, 720));
        Assert.Null(PictureGeometry.Downscale(0, 0, 720));
    }

    [Fact]
    public void No_target_means_no_transform()
    {
        Assert.Null(PictureGeometry.Downscale(1920, 1080, null));
        Assert.Null(PictureGeometry.Downscale(1920, 1080, 0));
    }

    [Fact]
    public void An_anamorphic_source_keeps_its_storage_aspect()
    {
        // Scaling the stored frame proportionally leaves the sample aspect ratio untouched, so the
        // display aspect survives without this code needing to know what it was.
        var size = PictureGeometry.Downscale(1440, 1080, 720);

        Assert.Equal(new PictureSize(960, 720), size);
    }
}
