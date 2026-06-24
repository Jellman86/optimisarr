using Optimisarr.Core.Scheduling;

namespace Optimisarr.Tests;

public sealed class AutoExclusionPolicyTests
{
    [Theory]
    [InlineData(0, 3, false)]   // no failures yet
    [InlineData(2, 3, false)]   // below the threshold
    [InlineData(3, 3, true)]    // reached the threshold
    [InlineData(5, 3, true)]    // past the threshold
    public void Excludes_only_once_the_failure_threshold_is_reached(int failures, int threshold, bool expected)
    {
        Assert.Equal(expected, AutoExclusionPolicy.ShouldExclude(failures, threshold));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_threshold_of_zero_or_less_disables_auto_exclusion(int threshold)
    {
        Assert.False(AutoExclusionPolicy.ShouldExclude(100, threshold));
    }
}
