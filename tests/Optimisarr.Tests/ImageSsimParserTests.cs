using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class ImageSsimParserTests
{
    [Fact]
    public void Parses_the_all_channel_ssim_from_a_stats_line()
    {
        // ffmpeg's ssim filter writes one line per frame; a single still produces one frame.
        const string stats = "n:1 Y:0.998765 U:0.997000 V:0.996500 All:0.998123 (27.123456)";

        var ssim = ImageSsimParser.Parse(stats);

        Assert.Equal(0.998123, ssim!.Value, 6);
    }

    [Fact]
    public void Parses_the_summary_line_written_to_stderr()
    {
        const string stderr =
            "[Parsed_ssim_0 @ 0x55] SSIM Y:0.991000 (20.4) U:0.99 V:0.99 All:0.985432 (18.3)";

        var ssim = ImageSsimParser.Parse(stderr);

        Assert.Equal(0.985432, ssim!.Value, 6);
    }

    [Fact]
    public void Uses_the_last_frame_when_several_are_present()
    {
        const string stats =
            "n:1 Y:0.9 U:0.9 V:0.9 All:0.900000 (10.0)\n" +
            "n:2 Y:0.8 U:0.8 V:0.8 All:0.800000 (7.0)";

        var ssim = ImageSsimParser.Parse(stats);

        Assert.Equal(0.800000, ssim!.Value, 6);
    }

    [Fact]
    public void Returns_null_when_no_all_value_is_present()
    {
        Assert.Null(ImageSsimParser.Parse("frame=1 fps=0.0 q=-0.0 size=0kB"));
        Assert.Null(ImageSsimParser.Parse(""));
    }
}
