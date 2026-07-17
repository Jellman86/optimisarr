using Optimisarr.Api.Queue;
using Optimisarr.Core.Domain;

namespace Optimisarr.Tests;

public sealed class QueueDispatcherSafetyTests
{
    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 0, false)]
    [InlineData(0, 1, false)]
    public void Fresh_track_cleanup_plan_must_still_contain_a_removal(
        int audioRemovals,
        int subtitleRemovals,
        bool shouldCancel)
    {
        Assert.Equal(
            shouldCancel,
            QueueDispatcher.TrackCleanupHasNoRemovalWork(
                RuleProfile.TrackCleanup,
                audioRemovals,
                subtitleRemovals));
    }

    [Fact]
    public void Other_profiles_are_not_cancelled_by_the_track_cleanup_guard()
    {
        Assert.False(QueueDispatcher.TrackCleanupHasNoRemovalWork(
            RuleProfile.RemuxCleanup,
            audioRemovalCount: 0,
            subtitleRemovalCount: 0));
    }
}
