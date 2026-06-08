using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class FfmpegProgressParserTests
{
    [Fact]
    public void Parses_time_fps_and_speed_from_a_progress_line()
    {
        const string line = "frame=  120 fps= 30 q=28.0 size=    1024kB time=00:01:04.00 bitrate= 131.1kbits/s speed=1.5x";

        var sample = FfmpegProgressParser.Parse(line);

        Assert.Equal(64, sample.ElapsedSeconds);
        Assert.Equal(30, sample.Fps);
        Assert.Equal(1.5, sample.Speed);
    }

    [Fact]
    public void Parses_fractional_seconds_and_hours()
    {
        var sample = FfmpegProgressParser.Parse("time=01:02:03.50 speed=2x");

        Assert.Equal((1 * 3600) + (2 * 60) + 3.5, sample.ElapsedSeconds);
    }

    [Fact]
    public void Treats_non_numeric_speed_and_missing_fields_as_unknown()
    {
        var sample = FfmpegProgressParser.Parse("time=00:00:02.00 bitrate=N/A speed=N/A");

        Assert.Equal(2, sample.ElapsedSeconds);
        Assert.Null(sample.Fps);
        Assert.Null(sample.Speed);
    }

    [Fact]
    public void Returns_all_null_for_a_non_progress_line()
    {
        var sample = FfmpegProgressParser.Parse("[hevc_nvenc @ 0x55] using cq=19");

        Assert.Null(sample.ElapsedSeconds);
        Assert.Null(sample.Fps);
        Assert.Null(sample.Speed);
    }

    [Fact]
    public void Estimates_remaining_wall_clock_seconds_from_speed()
    {
        // 100s of media, 40s done, encoding at 2x real time -> 60s media left at 2x = 30s wall.
        Assert.Equal(30, FfmpegProgressParser.EstimateRemainingSeconds(100, 40, 2));
    }

    [Fact]
    public void Remaining_is_zero_at_or_past_the_end_and_null_without_usable_speed()
    {
        Assert.Equal(0, FfmpegProgressParser.EstimateRemainingSeconds(100, 100, 1));
        Assert.Null(FfmpegProgressParser.EstimateRemainingSeconds(100, 40, 0));
        Assert.Null(FfmpegProgressParser.EstimateRemainingSeconds(0, 0, 2));
    }
}
