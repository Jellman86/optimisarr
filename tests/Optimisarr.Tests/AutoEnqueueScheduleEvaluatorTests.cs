using Optimisarr.Core.Scheduling;

namespace Optimisarr.Tests;

public sealed class AutoEnqueueScheduleEvaluatorTests
{
    private static DateTime At(int day, int hour, int minute = 0) => new(2026, 6, day, hour, minute, 0, DateTimeKind.Local);

    private static readonly TimeOnly NightStart = new(1, 0);
    private static readonly TimeOnly NightEnd = new(6, 0);

    [Fact]
    public void Disabled_is_never_due()
    {
        Assert.False(AutoEnqueueScheduleEvaluator.IsDue(
            enabled: false, NightStart, NightEnd, At(10, 2), lastRunLocal: null));
    }

    [Fact]
    public void Outside_the_window_is_not_due()
    {
        Assert.False(AutoEnqueueScheduleEvaluator.IsDue(
            enabled: true, NightStart, NightEnd, At(10, 9), lastRunLocal: null));
    }

    [Fact]
    public void Inside_the_window_with_no_prior_run_is_due()
    {
        Assert.True(AutoEnqueueScheduleEvaluator.IsDue(
            enabled: true, NightStart, NightEnd, At(10, 2), lastRunLocal: null));
    }

    [Fact]
    public void Does_not_run_twice_in_the_same_window_occurrence()
    {
        // Ran at 01:30; checking again at 03:00 the same night must not re-fire.
        Assert.False(AutoEnqueueScheduleEvaluator.IsDue(
            enabled: true, NightStart, NightEnd, At(10, 3), lastRunLocal: At(10, 1, 30)));
    }

    [Fact]
    public void Runs_again_on_the_next_night()
    {
        // Last ran during night of day 10; the day-11 window is a fresh occurrence.
        Assert.True(AutoEnqueueScheduleEvaluator.IsDue(
            enabled: true, NightStart, NightEnd, At(11, 2), lastRunLocal: At(10, 1, 30)));
    }

    [Fact]
    public void All_day_window_runs_once_per_calendar_day()
    {
        var allDay = new TimeOnly(0, 0);

        // First check of the day with yesterday's run pending -> due.
        Assert.True(AutoEnqueueScheduleEvaluator.IsDue(
            enabled: true, allDay, allDay, At(10, 14), lastRunLocal: At(9, 14)));

        // Already ran earlier today -> not due again.
        Assert.False(AutoEnqueueScheduleEvaluator.IsDue(
            enabled: true, allDay, allDay, At(10, 18), lastRunLocal: At(10, 9)));
    }

    [Fact]
    public void Midnight_crossing_window_treats_the_pre_midnight_open_as_current()
    {
        var start = new TimeOnly(22, 0);
        var end = new TimeOnly(6, 0);

        // 02:00 on day 11 belongs to the window that opened 22:00 on day 10.
        Assert.True(AutoEnqueueScheduleEvaluator.IsDue(
            enabled: true, start, end, At(11, 2), lastRunLocal: At(10, 12)));

        // Having run at 23:00 on day 10, the 02:00 check is the same occurrence -> not due.
        Assert.False(AutoEnqueueScheduleEvaluator.IsDue(
            enabled: true, start, end, At(11, 2), lastRunLocal: At(10, 23)));
    }
}
