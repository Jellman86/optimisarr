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
}
