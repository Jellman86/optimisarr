namespace Optimisarr.Core.Queue;

/// <summary>The operator-facing quality and the encoder-specific value actually passed to FFmpeg.</summary>
public sealed record EncoderQuality(int Requested, int Effective, string Mode, int RetryCount);

/// <summary>
/// Maps Optimisarr's software-oriented quality number onto each encoder family's own control.
/// FFmpeg and oneVPL document these as different codec-specific modes, so equal numbers must not be
/// presented as equivalent. Hardware encoders receive conservative headroom; a VMAF quality retry
/// moves one further step toward quality without changing the library's saved preference.
/// </summary>
public static class EncoderQualityPolicy
{
    private const int HardwareHeadroom = 4;
    private const int RetryStep = 3;

    public static EncoderQuality Resolve(string? encoder, int requested, int retryCount)
    {
        var boundedRequested = Math.Clamp(requested, 0, 51);
        var boundedRetry = Math.Max(0, retryCount);
        var (mode, headroom) = Family(encoder) switch
        {
            "qsv" => ("ICQ", HardwareHeadroom),
            "nvenc" => ("CQ", HardwareHeadroom),
            "vaapi" => ("QP", HardwareHeadroom),
            _ => ("CRF", 0)
        };
        var effective = Math.Max(0, boundedRequested - headroom - (boundedRetry * RetryStep));
        return new EncoderQuality(boundedRequested, effective, mode, boundedRetry);
    }

    private static string Family(string? encoder)
    {
        if (string.IsNullOrWhiteSpace(encoder))
        {
            return "cpu";
        }

        return encoder.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase) ? "qsv"
            : encoder.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase) ? "nvenc"
            : encoder.EndsWith("_vaapi", StringComparison.OrdinalIgnoreCase) ? "vaapi"
            : "cpu";
    }
}
