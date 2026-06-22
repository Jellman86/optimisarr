using Optimisarr.Core.Scheduling;

namespace Optimisarr.Tests;

public sealed class DispatchPolicyEvaluatorTests
{
    private static TimeOnly At(int h, int m = 0) => new(h, m);

    [Fact]
    public void Allows_starting_with_no_gates_tripped()
    {
        Assert.True(DispatchPolicyEvaluator.Evaluate(minFreeDiskBytes: 0, freeDiskBytes: null).CanStart);
    }

    [Fact]
    public void Blocks_when_free_disk_is_below_the_threshold()
    {
        var decision = DispatchPolicyEvaluator.Evaluate(minFreeDiskBytes: 10_000_000_000, freeDiskBytes: 2_000_000_000);

        Assert.False(decision.CanStart);
        Assert.NotNull(decision.BlockedReason);
    }

    [Fact]
    public void Allows_when_free_disk_meets_the_threshold()
    {
        Assert.True(DispatchPolicyEvaluator.Evaluate(10_000_000_000, 20_000_000_000).CanStart);
    }

    [Fact]
    public void Does_not_pause_when_free_disk_cannot_be_measured()
    {
        Assert.True(DispatchPolicyEvaluator.Evaluate(10_000_000_000, null).CanStart);
    }

    [Fact]
    public void A_zero_threshold_disables_the_disk_check()
    {
        Assert.True(DispatchPolicyEvaluator.Evaluate(0, 1).CanStart);
    }

    [Fact]
    public void Blocks_while_a_watched_service_is_active()
    {
        var decision = DispatchPolicyEvaluator.Evaluate(0, null, servicesActive: true, servicesActiveReason: "Plex is streaming.");

        Assert.False(decision.CanStart);
        Assert.Equal("Plex is streaming.", decision.BlockedReason);
    }

    // WithinWindow is the shared window check used for per-library auto-optimise windows.
    [Fact]
    public void WithinWindow_same_day_is_inclusive_of_start_and_exclusive_of_end()
    {
        Assert.True(DispatchPolicyEvaluator.WithinWindow(At(9), At(17), At(12)));
        Assert.True(DispatchPolicyEvaluator.WithinWindow(At(9), At(17), At(9)));
        Assert.False(DispatchPolicyEvaluator.WithinWindow(At(9), At(17), At(8, 59)));
        Assert.False(DispatchPolicyEvaluator.WithinWindow(At(9), At(17), At(17)));
    }

    [Theory]
    [InlineData(23, 30, true)]
    [InlineData(2, 0, true)]
    [InlineData(12, 0, false)]
    public void WithinWindow_spans_midnight(int hour, int minute, bool expected)
    {
        Assert.Equal(expected, DispatchPolicyEvaluator.WithinWindow(At(22), At(6), At(hour, minute)));
    }

    [Fact]
    public void WithinWindow_zero_length_means_all_day()
    {
        Assert.True(DispatchPolicyEvaluator.WithinWindow(At(0), At(0), At(3)));
    }
}
