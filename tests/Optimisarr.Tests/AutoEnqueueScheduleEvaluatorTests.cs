using Optimisarr.Core.Scheduling;

namespace Optimisarr.Tests;

public sealed class AutoEnqueueScheduleEvaluatorTests
{
    private static TimeOnly At(int h, int m = 0) => new(h, m);

    [Fact]
    public void Disabled_never_enqueues()
    {
        Assert.False(AutoEnqueueScheduleEvaluator.ShouldEnqueueNow(
            enabled: false, At(0), At(5), At(2)));
    }

    [Fact]
    public void Enqueues_when_inside_a_daytime_window()
    {
        Assert.True(AutoEnqueueScheduleEvaluator.ShouldEnqueueNow(
            enabled: true, At(9), At(17), At(12)));
    }

    [Fact]
    public void Does_not_enqueue_outside_the_window()
    {
        Assert.False(AutoEnqueueScheduleEvaluator.ShouldEnqueueNow(
            enabled: true, At(9), At(17), At(20)));
    }

    [Theory]
    [InlineData(2, true)]   // 02:00 is inside a 22:00–06:00 overnight window
    [InlineData(23, true)]  // 23:00 is inside
    [InlineData(8, false)]  // 08:00 is outside
    public void Handles_overnight_windows(int hour, bool expected)
    {
        Assert.Equal(expected, AutoEnqueueScheduleEvaluator.ShouldEnqueueNow(
            enabled: true, At(22), At(6), At(hour)));
    }

    [Fact]
    public void All_day_window_always_enqueues_when_enabled()
    {
        // start == end means "always".
        Assert.True(AutoEnqueueScheduleEvaluator.ShouldEnqueueNow(
            enabled: true, At(0), At(0), At(13)));
    }
}
