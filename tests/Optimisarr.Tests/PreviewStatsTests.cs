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

    [Fact]
    public void Rate_based_saving_uses_bitrate_so_a_short_clip_still_compares_fairly()
    {
        // Original: 1000 bytes over 100 s = 10 B/s. Clip: 30 bytes over 6 s = 5 B/s → 50% saving,
        // even though the clip is far smaller in absolute size.
        Assert.Equal(50.0, PreviewStats.SavingPercentByRate(1000, 100, 30, 6));
    }

    [Fact]
    public void Rate_based_saving_matches_size_based_for_equal_durations()
    {
        Assert.Equal(
            PreviewStats.SavingPercent(1000, 250),
            PreviewStats.SavingPercentByRate(1000, 60, 250, 60));
    }

    [Fact]
    public void Rate_based_saving_is_null_when_a_duration_is_missing_or_zero()
    {
        Assert.Null(PreviewStats.SavingPercentByRate(1000, null, 250, 60));
        Assert.Null(PreviewStats.SavingPercentByRate(1000, 60, 250, 0));
    }
}
