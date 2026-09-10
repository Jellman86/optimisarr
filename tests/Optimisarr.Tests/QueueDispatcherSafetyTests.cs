using Optimisarr.Api.Queue;
using Optimisarr.Api.Workers;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Data;

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

    // --- Restart recovery of a delivered remote candidate --------------------------------------

    [Fact]
    public void A_delivered_candidate_interrupted_mid_verification_is_kept_rather_than_discarded()
    {
        // The expensive part — the encode — happened on another machine and is finished. Only this
        // machine's verdict was interrupted, so the candidate waits for verification to run again.
        var delivered = RemoteCandidate.PathFor("/work/42", jobId: 7, extension: ".mkv");

        Assert.Equal(
            QueueDispatcher.RecoveryAction.KeepDelivered,
            QueueDispatcher.RecoveryActionFor(JobStatus.Verifying, delivered, attempt: 1));
    }

    [Fact]
    public void An_interrupted_local_verification_is_requeued_as_before()
    {
        // A local encode that was being verified has a half-trusted output and a source still
        // here; re-encoding is the honest recovery, exactly as it always was.
        Assert.Equal(
            QueueDispatcher.RecoveryAction.Requeue,
            QueueDispatcher.RecoveryActionFor(JobStatus.Verifying, "/work/42/Film.opt.mkv", attempt: 1));
    }

    [Fact]
    public void An_interrupted_local_job_out_of_attempts_is_failed()
    {
        Assert.Equal(
            QueueDispatcher.RecoveryAction.Fail,
            QueueDispatcher.RecoveryActionFor(JobStatus.Transcoding, "/work/42/Film.opt.mkv", attempt: 1_000));
    }

    [Fact]
    public void A_delivered_candidate_is_recognised_by_its_name_and_a_local_output_is_not()
    {
        Assert.True(RemoteCandidate.IsDelivered(RemoteCandidate.PathFor("/work/1", 3, ".mp4")));
        Assert.False(RemoteCandidate.IsDelivered("/work/1/Film.opt.mp4"));
        Assert.False(RemoteCandidate.IsDelivered(null));
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
