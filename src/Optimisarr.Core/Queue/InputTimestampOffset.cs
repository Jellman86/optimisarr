using Optimisarr.Core.Tools;
using System.Globalization;

namespace Optimisarr.Core.Queue;

/// <summary>Corrects a declared input start that lies after its first reconstructed pictures.</summary>
public sealed class InputTimestampOffset(string ffprobeCommand = "ffprobe")
{
    public const double MaximumSeconds = 86400;

    public static double Calculate(double? declaredStart, string presentationTimes)
    {
        var start = declaredStart ?? 0;
        if (!double.IsFinite(start)) throw new ArgumentException("The declared input start is not finite.");
        var times = presentationTimes.Split('\n').Select(line => double.TryParse(line.Trim().TrimEnd(','),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? (double?)value : null)
            .Where(value => value is not null).Select(value => value!.Value).ToArray();
        if (times.Length == 0) throw new InvalidOperationException("No initial picture timestamps were available for safe encoding.");
        var offset = Math.Max(0, start - times.Min());
        if (offset > MaximumSeconds) throw new InvalidOperationException("The source's initial timing correction exceeds one day.");
        return offset;
    }

    public static IReadOnlyList<string> Arguments(string path) =>
    [
        "-v", "error", "-fflags", "+genpts", "-select_streams", "V:0", "-read_intervals", "%+#128",
        "-show_entries", "packet=pts_time", "-of", "csv=p=0", path
    ];

    public async Task<double> MeasureAsync(string path, double? declaredStart, CancellationToken cancellationToken)
    {
        var result = await BoundedToolProcess.RunAsync(ffprobeCommand, Arguments(path), cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("The source's initial timestamp probe failed: " + result.Error);
        return Calculate(declaredStart, result.Output);
    }
}
