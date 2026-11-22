using Optimisarr.Core.Queue;

namespace Optimisarr.Core.SuperResolution;

/// <summary>
/// Sizes a super-resolution batch. One batch of source pictures and its upscaled pictures are the
/// only frames on disk at any time, so scratch is bounded by the batch, never by the length of the
/// video. The size assumes raw RGB for both, an upper bound for PNG, and stays within a fixed
/// working budget and above the operator's free-space reserve. Null means not even a minimum batch
/// fits, and the job is refused rather than allowed to fill the disk.
/// </summary>
public static class SuperResolutionBatchPlan
{
    public const int MinimumFrames = 8;
    public const int MaximumFrames = 240;
    public const long BudgetBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>A small allowance for PNG framing on top of the raw picture bytes.</summary>
    private const long PerPictureOverheadBytes = 64 * 1024;

    public static long WorstCaseBytesPerFrame(PictureSize source, int scale)
    {
        var sourceBytes = (long)source.Width * source.Height * 3;
        var upscaledBytes = (long)source.Width * scale * source.Height * scale * 3;
        return sourceBytes + upscaledBytes + 2 * PerPictureOverheadBytes;
    }

    public static int? FramesPerBatch(PictureSize source, int scale, long freeBytes, long reserveBytes)
    {
        var available = Math.Min(freeBytes - reserveBytes, BudgetBytes);
        if (available <= 0)
        {
            return null;
        }
        var frames = available / WorstCaseBytesPerFrame(source, scale);
        return frames < MinimumFrames ? null : (int)Math.Min(frames, MaximumFrames);
    }

    /// <summary>
    /// Returns the probed frame count when the container provides one, otherwise estimates it from
    /// duration and frame rate so Matroska files without a NUMBER_OF_FRAMES statistics tag still
    /// report super-resolution progress instead of staying at 0%.
    /// </summary>
    public static int? EstimateExpectedFrames(int? probedFrameCount, double? durationSeconds, double? frameRate)
    {
        if (probedFrameCount is > 0)
        {
            return probedFrameCount.Value;
        }

        if (durationSeconds is > 0 and var duration && double.IsFinite(duration)
            && frameRate is > 0 and var fps && double.IsFinite(fps))
        {
            var estimated = Math.Round(duration * fps, MidpointRounding.AwayFromZero);
            if (estimated >= 1 && estimated <= int.MaxValue)
            {
                return (int)estimated;
            }
        }

        return null;
    }
}
