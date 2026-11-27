using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class KeyframeIntervalTests
{
    [Theory]
    [InlineData(10, 24.0, 240)]
    [InlineData(10, 24000.0 / 1001, 240)]
    [InlineData(2, 60000.0 / 1001, 120)]
    [InlineData(1, 25.0, 25)]
    [InlineData(20, 50.0, 1000)]
    public void Seconds_become_frames_at_the_rate_the_encode_produces(int seconds, double frameRate, int frames) =>
        Assert.Equal(frames, KeyframeInterval.Frames(seconds, frameRate));

    [Fact]
    public void The_encoder_default_sends_no_interval() => Assert.Null(KeyframeInterval.Frames(null, 24));

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-25.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void An_unknown_frame_rate_falls_back_to_the_encoder_default(double? frameRate) =>
        Assert.Null(KeyframeInterval.Frames(10, frameRate));

    [Fact]
    public void A_very_slow_source_still_gets_at_least_one_frame_per_interval() =>
        Assert.Equal(1, KeyframeInterval.Frames(1, 0.2));

    [Fact]
    public void The_longest_interval_at_the_fastest_probed_rate_is_the_largest_frame_count() =>
        Assert.Equal(KeyframeInterval.MaximumFrames, KeyframeInterval.Frames(KeyframeInterval.MaximumSeconds, 1_000));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(10, true)]
    [InlineData(20, true)]
    [InlineData(21, false)]
    public void Only_whole_seconds_within_the_bounds_are_accepted(int seconds, bool valid) =>
        Assert.Equal(valid, KeyframeInterval.IsValidSeconds(seconds));

    [Fact]
    public void Settings_outside_the_bounds_are_never_turned_into_frames()
    {
        Assert.Null(KeyframeInterval.Frames(0, 24));
        Assert.Null(KeyframeInterval.Frames(KeyframeInterval.MaximumSeconds + 1, 24));
    }
}
