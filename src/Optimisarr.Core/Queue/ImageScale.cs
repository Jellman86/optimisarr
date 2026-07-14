using System.Globalization;

namespace Optimisarr.Core.Queue;

/// <summary>How a library downscales images, if at all.</summary>
public enum ImageDownscaleMode
{
    /// <summary>Keep original dimensions; no resize.</summary>
    None = 0,

    /// <summary>Fit the image within a maximum long-edge length in pixels, keeping aspect ratio.</summary>
    MaxLongEdge = 1,

    /// <summary>Scale both dimensions to a percentage of the original.</summary>
    Percent = 2
}

/// <summary>
/// Pure construction of the ffmpeg <c>scale</c> filter expression that downscales a still.
/// Every mode keeps the original aspect ratio and <em>never upscales</em> (the output is
/// capped at the source dimensions), and every result is forced to even dimensions for broad
/// encoder/decoder compatibility. Returns <c>null</c> when no resize is requested.
/// </summary>
public static class ImageScale
{
    /// <summary>The named long-edge caps the UI offers, in pixels.</summary>
    public const int LongEdge4K = 3840;
    public const int LongEdge1080p = 1920;

    public static string? BuildFilter(ImageDownscaleMode mode, int value)
    {
        switch (mode)
        {
            case ImageDownscaleMode.MaxLongEdge when value > 0:
                // Cap whichever edge is longer at `value`, keeping the other proportional (-2 keeps
                // aspect and yields an even length). `min(...)` guarantees we never enlarge a
                // picture already smaller than the cap.
                var l = value.ToString(CultureInfo.InvariantCulture);
                return $"scale=w='if(gt(iw,ih),min(iw,{l}),-2)':h='if(gt(iw,ih),-2,min(ih,{l}))':flags=lanczos";

            case ImageDownscaleMode.Percent when value is > 0 and < 100:
                // Scale both edges to `value`% of the source, truncated to an even number of pixels.
                var p = value.ToString(CultureInfo.InvariantCulture);
                return $"scale=w='trunc(iw*{p}/200)*2':h='trunc(ih*{p}/200)*2':flags=lanczos";

            default:
                // None, a non-positive cap, or a percentage of 100 (or more) is a no-op.
                return null;
        }
    }
}
