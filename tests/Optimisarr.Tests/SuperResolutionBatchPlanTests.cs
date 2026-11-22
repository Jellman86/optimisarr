using Optimisarr.Core.Queue;
using Optimisarr.Core.SuperResolution;

namespace Optimisarr.Tests;

/// <summary>
/// Super-resolution holds one batch of source frames and its upscaled frames on disk at a time.
/// The batch is sized from the free space, assuming the worst case of raw RGB pictures, so scratch
/// can never grow with the length of the video; a job that cannot fit even a small batch is refused.
/// </summary>
public sealed class SuperResolutionBatchPlanTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void A_batch_is_capped_by_the_working_budget_even_with_plenty_of_space()
    {
        var frames = SuperResolutionBatchPlan.FramesPerBatch(new PictureSize(1920, 1080), scale: 2, freeBytes: 2000 * GiB, reserveBytes: 10 * GiB);
        Assert.NotNull(frames);
        Assert.InRange(frames!.Value, SuperResolutionBatchPlan.MinimumFrames, SuperResolutionBatchPlan.MaximumFrames);
        Assert.True(frames.Value * SuperResolutionBatchPlan.WorstCaseBytesPerFrame(new PictureSize(1920, 1080), 2) <= SuperResolutionBatchPlan.BudgetBytes);
    }

    [Fact]
    public void Less_free_space_means_smaller_batches()
    {
        var roomy = SuperResolutionBatchPlan.FramesPerBatch(new PictureSize(1920, 1080), 2, 50 * GiB, 10 * GiB)!.Value;
        var tight = SuperResolutionBatchPlan.FramesPerBatch(new PictureSize(1920, 1080), 2, 11 * GiB, 10 * GiB)!.Value;
        Assert.True(tight < roomy);
        Assert.True(tight * SuperResolutionBatchPlan.WorstCaseBytesPerFrame(new PictureSize(1920, 1080), 2) <= 1 * GiB);
    }

    [Fact]
    public void A_job_that_cannot_fit_a_minimum_batch_above_the_reserve_is_refused()
    {
        Assert.Null(SuperResolutionBatchPlan.FramesPerBatch(new PictureSize(3840, 2160), 4, freeBytes: 10 * GiB + 1024, reserveBytes: 10 * GiB));
    }

    [Fact]
    public void A_frame_needs_room_for_its_source_and_its_upscaled_picture()
    {
        Assert.True(SuperResolutionBatchPlan.WorstCaseBytesPerFrame(new PictureSize(100, 100), 2) >= 100 * 100 * 3 + 200 * 200 * 3);
    }

    [Fact]
    public void Expected_frames_uses_probed_count_when_available()
    {
        Assert.Equal(42_000, SuperResolutionBatchPlan.EstimateExpectedFrames(42_000, durationSeconds: 1800, frameRate: 25));
    }

    [Fact]
    public void Expected_frames_falls_back_to_duration_times_frame_rate_when_container_omits_frame_count()
    {
        // Matroska files without a NUMBER_OF_FRAMES statistics tag leave FrameCount null,
        // so super-resolution progress falls back to duration * frame rate rather than staying at 0%.
        Assert.Equal(90_000, SuperResolutionBatchPlan.EstimateExpectedFrames(null, durationSeconds: 3600, frameRate: 25));
        Assert.Equal(1, SuperResolutionBatchPlan.EstimateExpectedFrames(0, durationSeconds: 0.04, frameRate: 25));
    }

    [Theory]
    [InlineData(null, null, 25.0)]
    [InlineData(null, 0.0, 25.0)]
    [InlineData(null, -10.0, 25.0)]
    [InlineData(null, 120.0, null)]
    [InlineData(null, 120.0, 0.0)]
    [InlineData(null, double.NaN, 25.0)]
    [InlineData(null, double.PositiveInfinity, 25.0)]
    public void Expected_frames_returns_null_when_duration_or_frame_rate_is_unusable(int? probed, double? duration, double? fps)
    {
        Assert.Null(SuperResolutionBatchPlan.EstimateExpectedFrames(probed, duration, fps));
    }
}
