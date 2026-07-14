namespace Optimisarr.Core.Verification;

public sealed record ImageQualityMeasurementContext(
    int ReferenceWidth,
    int ReferenceHeight,
    bool ReferenceMayHaveAlpha);

public sealed record ImageQualityCommand(
    IReadOnlyList<string> Arguments,
    string FilterGraph);

/// <summary>Builds the canonical, shell-free still-image SSIM invocation.</summary>
public static class ImageQualityCommandBuilder
{
    public static ImageQualityCommand Build(
        string distortedPath,
        string referencePath,
        string logPath,
        ImageQualityMeasurementContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distortedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(referencePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(context.ReferenceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(context.ReferenceHeight);

        var pixelFormat = context.ReferenceMayHaveAlpha ? "gbrap" : "gbrp";
        var normalise =
            $"scale={context.ReferenceWidth}:{context.ReferenceHeight}:" +
            "flags=bicubic:in_range=auto:out_range=full," +
            $"format={pixelFormat}";
        var filter =
            $"[0:v]settb=AVTB,setpts=PTS-STARTPTS,{normalise}[dist];" +
            $"[1:v]settb=AVTB,setpts=PTS-STARTPTS,{normalise}[ref];" +
            $"[dist][ref]ssim=stats_file={logPath}:shortest=1:repeatlast=0";

        IReadOnlyList<string> arguments =
        [
            "-nostdin",
            "-v", "error",
            "-i", distortedPath,
            "-i", referencePath,
            "-lavfi", filter,
            "-f", "null",
            "-"
        ];

        return new ImageQualityCommand(arguments, filter);
    }
}
