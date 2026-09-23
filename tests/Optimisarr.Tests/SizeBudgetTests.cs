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
