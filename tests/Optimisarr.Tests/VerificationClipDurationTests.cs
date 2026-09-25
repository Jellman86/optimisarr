using Optimisarr.Api.Queue;

namespace Optimisarr.Tests;

public sealed class VerificationClipDurationTests
{
    [Theory]
    [InlineData(8.0, 0, 8.0)]
    [InlineData(42.5, 0, 42.5)]
    [InlineData(120.0, 30, 60.0)]
    [InlineData(65.5, 30, 35.5)]
    [InlineData(null, 0, 60)]
    public void Verification_never_demands_more_video_than_exists_in_the_requested_window(
        double? sourceDuration, int start, double expected)
    {
        Assert.Equal(expected, new VerificationClip(60, start, "reference.mkv").ExpectedDuration(sourceDuration));
    }
}
