using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class FailureClassifierTests
{
    [Theory]
    [InlineData("Verification failed: Size saving", FailureCategory.SizeSaving)]
    [InlineData("Verification failed: A/V sync", FailureCategory.Verification)]
    [InlineData("Could not find tag for codec none in stream #37", FailureCategory.ContainerIncompatibility)]
    [InlineData("Subtitle encoding currently only possible from text to text or bitmap to bitmap",
        FailureCategory.BitmapSubtitles)]
    [InlineData("Replacement would collide with an existing file at /data/x.mp4", FailureCategory.ReplacementCollision)]
    [InlineData("The verified output is missing from the work directory.", FailureCategory.SourceMissing)]
    [InlineData("The original file no longer exists: /data/x.mkv", FailureCategory.SourceMissing)]
    [InlineData("ffmpeg exited with code 1: something unrecognised", FailureCategory.Other)]
    public void Classifies_known_failure_messages(string message, FailureCategory expected)
    {
        Assert.Equal(expected, FailureClassifier.Classify(message));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Treats_a_missing_message_as_other(string? message)
    {
        Assert.Equal(FailureCategory.Other, FailureClassifier.Classify(message));
    }

    [Fact]
    public void Every_category_has_a_human_description()
    {
        foreach (var category in Enum.GetValues<FailureCategory>())
        {
            Assert.False(string.IsNullOrWhiteSpace(FailureClassifier.Describe(category)));
        }
    }
}
