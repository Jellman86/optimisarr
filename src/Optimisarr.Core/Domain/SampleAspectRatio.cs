using System.Globalization;

namespace Optimisarr.Core.Domain;

/// <summary>
/// The shape of one stored pixel, as ffprobe reports it in <c>sample_aspect_ratio</c>. A PAL
/// widescreen DVD stores 720x576 pixels of shape 64:45, which players stretch to 1024x576.
/// </summary>
public readonly record struct SampleAspectRatio(int Numerator, int Denominator)
{
    /// <summary>
    /// Pixel shapes closer than this are the same shape. A downscale nudges the ratio when the new
    /// size rounds (1:1 can become 1280:1281); the closest pair of real shapes, 10:11 and 8:9, is
    /// about 2% apart.
    /// </summary>
    private const double SameShapeTolerance = 0.01;

    public double Ratio => Numerator / (double)Denominator;

    public bool IsSquare => Numerator == Denominator;

    /// <summary>The ratio as FFmpeg's <c>setsar</c> filter takes it.</summary>
    public string FilterValue => string.Create(CultureInfo.InvariantCulture, $"{Numerator}/{Denominator}");

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Numerator}:{Denominator}");

    /// <summary>The ratio ffprobe reported, or null when it reported none: "0:1", "N/A" or nothing.</summary>
    public static SampleAspectRatio? Parse(string? text)
    {
        var parts = text?.Split(':');
        return parts is [var numerator, var denominator]
            && int.TryParse(numerator, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
            && int.TryParse(denominator, NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d > 0
                ? new SampleAspectRatio(n, d)
                : null;
    }

    /// <summary>Whether two pixels have the same shape; an unreported shape is square.</summary>
    public static bool SameShape(SampleAspectRatio? first, SampleAspectRatio? second)
    {
        var a = first?.Ratio ?? 1;
        var b = second?.Ratio ?? 1;
        return Math.Abs(a - b) <= a * SameShapeTolerance;
    }
}
