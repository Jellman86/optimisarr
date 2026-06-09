using Optimisarr.Core.Scheduling;

namespace Optimisarr.Tests;

public sealed class DispatchPolicyEvaluatorTests
{
    private static readonly TimeOnly Midnight = new(0, 0);

    private static DispatchDecision Evaluate(
        bool scheduleEnabled = false,
        string start = "00:00",
        string end = "00:00",
        string now = "12:00",
        long minFreeDiskBytes = 0,
        long? freeDiskBytes = null,
        bool servicesActive = false,
        string? servicesActiveReason = null) =>
        DispatchPolicyEvaluator.Evaluate(
            scheduleEnabled,
            TimeOnly.Parse(start),
            TimeOnly.Parse(end),
            TimeOnly.Parse(now),
            minFreeDiskBytes,
            freeDiskBytes,
            servicesActive,
            servicesActiveReason);

    [Fact]
    public void Allows_starting_when_scheduling_is_disabled()
    {
        Assert.True(Evaluate(scheduleEnabled: false).CanStart);
    }

    [Fact]
    public void Same_day_window_allows_inside_and_blocks_outside()
    {
        Assert.True(Evaluate(true, "09:00", "17:00", now: "12:00").CanStart);
        Assert.False(Evaluate(true, "09:00", "17:00", now: "08:59").CanStart);
        Assert.False(Evaluate(true, "09:00", "17:00", now: "17:00").CanStart);  // end is exclusive
    }

    [Fact]
    public void Overnight_window_spans_midnight()
    {
        Assert.True(Evaluate(true, "22:00", "06:00", now: "23:30").CanStart);
        Assert.True(Evaluate(true, "22:00", "06:00", now: "02:00").CanStart);
        Assert.False(Evaluate(true, "22:00", "06:00", now: "12:00").CanStart);
    }

    [Fact]
    public void A_zero_length_window_means_all_day()
    {
        Assert.True(DispatchPolicyEvaluator.WithinWindow(Midnight, Midnight, new TimeOnly(3, 0)));
    }

    [Fact]
    public void Blocks_when_free_disk_is_below_the_threshold()
    {
        var decision = Evaluate(minFreeDiskBytes: 10_000_000_000, freeDiskBytes: 2_000_000_000);

        Assert.False(decision.CanStart);
        Assert.NotNull(decision.BlockedReason);
    }

    [Fact]
    public void Allows_when_free_disk_meets_the_threshold()
    {
        Assert.True(Evaluate(minFreeDiskBytes: 10_000_000_000, freeDiskBytes: 20_000_000_000).CanStart);
    }

    [Fact]
    public void Does_not_pause_when_free_disk_cannot_be_measured()
    {
        Assert.True(Evaluate(minFreeDiskBytes: 10_000_000_000, freeDiskBytes: null).CanStart);
    }

    [Fact]
    public void A_zero_threshold_disables_the_disk_check()
    {
        Assert.True(Evaluate(minFreeDiskBytes: 0, freeDiskBytes: 1).CanStart);
    }

    [Fact]
    public void Blocks_while_a_watched_service_is_active()
    {
        var decision = Evaluate(servicesActive: true, servicesActiveReason: "Plex is streaming.");

        Assert.False(decision.CanStart);
        Assert.Equal("Plex is streaming.", decision.BlockedReason);
    }

    [Fact]
    public void Does_not_block_when_no_watched_service_is_active()
    {
        Assert.True(Evaluate(servicesActive: false).CanStart);
    }

    [Fact]
    public void The_window_takes_priority_over_an_active_service()
    {
        var decision = Evaluate(
            scheduleEnabled: true, start: "09:00", end: "17:00", now: "03:00",
            servicesActive: true, servicesActiveReason: "Plex is streaming.");

        Assert.False(decision.CanStart);
        Assert.Contains("processing window", decision.BlockedReason);
    }

    [Fact]
    public void The_window_takes_priority_in_the_reason_when_both_would_block()
    {
        var decision = Evaluate(
            scheduleEnabled: true, start: "09:00", end: "17:00", now: "03:00",
            minFreeDiskBytes: 10_000_000_000, freeDiskBytes: 1);

        Assert.False(decision.CanStart);
        Assert.Contains("processing window", decision.BlockedReason);
    }
}
