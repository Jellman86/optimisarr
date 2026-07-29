using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class PreviewClipPolicyTests
{
    [Fact]
    public void Long_video_preview_uses_a_reproducible_middle_window()
    {
        var clip = PreviewClipPolicy.Plan(1_296.91);

        Assert.Equal(618, clip.StartSeconds);
        Assert.Equal(60, clip.DurationSeconds);
        Assert.True(clip.IsClipped);
    }

    [Fact]
    public void Short_video_preview_starts_at_zero_and_is_not_marked_as_clipped()
    {
        var clip = PreviewClipPolicy.Plan(42.5);

        Assert.Equal(0, clip.StartSeconds);
        Assert.Equal(60, clip.DurationSeconds);
        Assert.False(clip.IsClipped);
    }
}
