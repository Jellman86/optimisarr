using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class FfmpegProgressParserTests
{
    [Fact]
    public void Protocol_parser_emits_a_sample_at_each_progress_boundary()
    {
        var parser = new FfmpegProgressProtocolParser();

        Assert.Null(parser.ParseLine("frame=120"));
        Assert.Null(parser.ParseLine("fps=29.97"));
        Assert.Null(parser.ParseLine("out_time=01:02:03.500000"));
        Assert.Null(parser.ParseLine("speed=1.5x"));
        var sample = parser.ParseLine("progress=continue");

        Assert.NotNull(sample);
        Assert.Equal((1 * 3600) + (2 * 60) + 3.5, sample.ElapsedSeconds);
        Assert.Equal(29.97, sample.Fps);
        Assert.Equal(1.5, sample.Speed);
    }

    [Fact]
    public void Protocol_parser_supports_microsecond_fallbacks_from_old_and_new_ffmpeg()
    {
        var current = new FfmpegProgressProtocolParser();
        current.ParseLine("out_time_us=2500000");

        var legacy = new FfmpegProgressProtocolParser();
        legacy.ParseLine("out_time_ms=3750000");

        Assert.Equal(2.5, current.ParseLine("progress=continue")!.ElapsedSeconds);
        Assert.Equal(3.75, legacy.ParseLine("progress=end")!.ElapsedSeconds);
    }

    [Fact]
    public void Protocol_parser_ignores_malformed_values_and_does_not_leak_fields_between_blocks()
    {
        var parser = new FfmpegProgressProtocolParser();
        parser.ParseLine("out_time=00:00:04.000000");
        parser.ParseLine("fps=not-a-number");
        parser.ParseLine("speed=N/A");
        var first = parser.ParseLine("progress=continue");

        parser.ParseLine("unknown=value=with=equals");
        var second = parser.ParseLine("progress=end");

        Assert.Equal(4, first!.ElapsedSeconds);
        Assert.Null(first.Fps);
        Assert.Null(first.Speed);
        Assert.NotNull(second);
        Assert.Null(second.ElapsedSeconds);
        Assert.Null(second.Fps);
        Assert.Null(second.Speed);
    }

    [Fact]
    public void Protocol_parser_prefers_the_unambiguous_timestamp_and_tolerates_whitespace()
    {
        var parser = new FfmpegProgressProtocolParser();
        parser.ParseLine("out_time_us=1000000");
        parser.ParseLine(" out_time = 00:00:02.250000\r");
        parser.ParseLine(" fps = 30 ");
        parser.ParseLine(" speed = 2x ");

        var sample = parser.ParseLine(" progress = continue ");

        Assert.Equal(2.25, sample!.ElapsedSeconds);
        Assert.Equal(30, sample.Fps);
        Assert.Equal(2, sample.Speed);
    }

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
