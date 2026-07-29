using Optimisarr.Api.Queue;

namespace Optimisarr.Tests;

public sealed class AdaptiveQualityDurationTests
{
    [Fact]
    public void Primary_picture_duration_wins_when_subtitles_extend_the_container()
    {
        var duration = AdaptiveQualityDuration.Resolve(
            videoDurationSeconds: 1_405.321,
            containerDurationSeconds: 3_896.275);

        Assert.Equal(1_405.321, duration);
    }

    [Theory]
    [InlineData(null, 1_405.321, 1_405.321)]
    [InlineData(double.NaN, 1_405.321, 1_405.321)]
    [InlineData(0d, 1_405.321, 1_405.321)]
    [InlineData(null, null, null)]
    public void Invalid_or_missing_picture_duration_uses_the_available_container_fallback(
        double? videoDurationSeconds,
        double? containerDurationSeconds,
        double? expected)
    {
        Assert.Equal(
            expected,
            AdaptiveQualityDuration.Resolve(videoDurationSeconds, containerDurationSeconds));
    }
}
