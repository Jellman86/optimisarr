namespace Optimisarr.Core.Queue;

/// <summary>Exact output dimensions a picture transform will produce.</summary>
public sealed record PictureSize(int Width, int Height);

/// <summary>
/// Decides the exact dimensions an encode will produce, so that one number feeds both the
/// FFmpeg filter and the verification gate.
///
/// This exists because the alternative is fragile in a way that only fails in production. Handing
/// FFmpeg <c>scale=-2:720</c> and asking the verifier to check "roughly 720p" leaves two places
/// each guessing how the width was rounded, and an off-by-two disagreement fails a perfectly good
/// encode — or, worse, a tolerance wide enough to hide it hides a real resize bug too. Computing
/// the width here and emitting <c>scale=W:H</c> explicitly means the filter and the gate cannot
/// disagree, because they were never asked to agree.
/// </summary>
public static class PictureGeometry
{
    /// <summary>
    /// The size a proportional downscale to <paramref name="targetHeight"/> produces, or
    /// <c>null</c> when no scaling should happen: the source is already at or below the target,
    /// or its dimensions are unknown. Scaling up is never done — a downscale exists to save
    /// space, and upscaling a source spends bits to invent nothing.
    /// </summary>
    public static PictureSize? Downscale(int? sourceWidth, int? sourceHeight, int? targetHeight)
    {
        if (targetHeight is not > 0
            || sourceWidth is not > 0
            || sourceHeight is not > 0
            || sourceHeight <= targetHeight)
        {
            return null;
        }

        var height = targetHeight.Value;
        var width = NearestEven(sourceWidth.Value * (double)height / sourceHeight.Value);
        return new PictureSize(width, height);
    }

    /// <summary>The <c>scale</c> filter that produces exactly <paramref name="size"/>.</summary>
    public static string ScaleFilter(PictureSize size) =>
        $"scale={size.Width}:{size.Height}:flags=lanczos";

    // Matches what FFmpeg's own scale=-2:H would choose, measured rather than assumed: 853.33
    // becomes 854, 1725.84 becomes 1726, 1150.56 becomes 1150. Nearest even keeps the aspect
    // closest to the source; even is required by 4:2:0 chroma subsampling. AwayFromZero so a
    // value landing exactly on an odd number rounds the same way every time.
    private static int NearestEven(double value) =>
        2 * (int)Math.Round(value / 2, MidpointRounding.AwayFromZero);
}
