using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;

namespace Optimisarr.Tests;

public sealed class MediaTimelineDurationTests
{
    [Fact]
    public void Primary_picture_duration_wins_when_subtitles_extend_the_container()
    {
        var duration = MediaTimelineDuration.Resolve(
            MediaKind.Video,
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
            MediaTimelineDuration.Resolve(
                MediaKind.Video,
                videoDurationSeconds,
                containerDurationSeconds));
    }

    [Fact]
    public void Audio_uses_its_container_timeline_even_if_a_picture_duration_is_supplied()
    {
        Assert.Equal(
            180.5,
            MediaTimelineDuration.Resolve(
                MediaKind.Audio,
                videoDurationSeconds: 120,
                containerDurationSeconds: 180.5));
    }
}
