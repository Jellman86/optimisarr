using Optimisarr.Api.Queue;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;

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

    [Fact]
    public void Calibration_uses_its_concrete_preset_quality_without_an_adaptive_search()
    {
        Assert.False(QueueDispatcher.ShouldSelectAdaptiveQuality(
            VideoQualityStrategy.AdaptiveVmaf,
            hasVideoCodec: true,
            hasVideoQuality: true,
            hasAdaptiveQuality: false,
            isCalibration: true));
    }

    [Fact]
    public void Normal_and_preview_jobs_keep_the_selected_adaptive_quality_path()
    {
        Assert.True(QueueDispatcher.ShouldSelectAdaptiveQuality(
            VideoQualityStrategy.AdaptiveVmaf,
            hasVideoCodec: true,
            hasVideoQuality: true,
            hasAdaptiveQuality: false,
            isCalibration: false));
    }
}
