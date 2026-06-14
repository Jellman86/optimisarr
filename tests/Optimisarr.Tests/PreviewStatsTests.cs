using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class PreviewStatsTests
{
    [Fact]
    public void Reports_a_positive_percentage_when_the_encode_is_smaller()
    {
        Assert.Equal(75.0, PreviewStats.SavingPercent(1000, 250));
    }

    [Fact]
    public void Reports_a_negative_percentage_when_the_encode_is_larger()
    {
        Assert.Equal(-20.0, PreviewStats.SavingPercent(1000, 1200));
    }

    [Fact]
    public void Is_null_when_a_size_is_missing_or_the_original_is_empty()
    {
        Assert.Null(PreviewStats.SavingPercent(null, 100));
        Assert.Null(PreviewStats.SavingPercent(1000, null));
        Assert.Null(PreviewStats.SavingPercent(0, 100));
    }
}
