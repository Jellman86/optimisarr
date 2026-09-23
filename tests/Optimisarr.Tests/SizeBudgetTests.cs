using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class SizeBudgetTests
{
    [Fact]
    public void Required_reduction_sets_the_largest_allowed_candidate_one_byte_below_source()
    {
        Assert.Equal(999L, SizeBudget.MaxCandidateBytes(1000, requireReduction: true, disposable: false));
        Assert.False(SizeBudget.Exceeded(999, 999));
        Assert.True(SizeBudget.Exceeded(1000, 999));
    }

    [Fact]
    public void Minimum_useful_saving_sets_the_same_early_stop_limit_as_final_verification()
    {
        var maximum = SizeBudget.MaxCandidateBytes(1000, true, false, minimumSavingPercent: 10);

        Assert.Equal(900L, maximum);
        Assert.False(SizeBudget.Exceeded(900, maximum!.Value));
        Assert.True(SizeBudget.Exceeded(901, maximum.Value));
        Assert.Equal(6L, SizeBudget.MaxCandidateBytes(7, true, false, minimumSavingPercent: 10));
    }

    [Fact]
    public void Compatibility_and_disposable_work_ignore_a_minimum_saving_target()
    {
        Assert.Null(SizeBudget.MaxCandidateBytes(1000, false, false, minimumSavingPercent: 10));
        Assert.Null(SizeBudget.MaxCandidateBytes(1000, true, true, minimumSavingPercent: 10));
    }

    [Theory]
    [InlineData(false, false, 1000)]
    [InlineData(true, true, 1000)]
    [InlineData(true, false, 0)]
    public void Work_without_a_real_size_saving_gate_has_no_early_stop(
        bool requireReduction, bool disposable, long sourceBytes)
    {
        Assert.Null(SizeBudget.MaxCandidateBytes(sourceBytes, requireReduction, disposable));
    }
}
