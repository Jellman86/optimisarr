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
        // The time is the first CSV field; a decoded frame can append side-data notes after it.
        var times = presentationTimes.Split('\n').Select(line => double.TryParse(line.Split(',')[0].Trim(),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? (double?)value : null)
            .Where(value => value is not null).Select(value => value!.Value).ToArray();
        if (times.Length == 0) throw new InvalidOperationException("No initial picture timestamps were available for safe encoding.");
        var offset = Math.Max(0, start - times.Min());
        if (offset > MaximumSeconds) throw new InvalidOperationException("The source's initial timing correction exceeds one day.");
        return offset;
    }

    /// <param name="generatedPresentationTimes">The encode's own timestamp mode. With +genpts the
    /// reconstructed packet times are what the encoder sees; without it, the decoder's picture times
    /// are, and they can start a picture earlier (decode-only video without B-frames).</param>
    public static IReadOnlyList<string> Arguments(string path, bool generatedPresentationTimes = true) =>
        generatedPresentationTimes
            ?
            [
                "-v", "error", "-fflags", "+genpts", "-select_streams", "V:0", "-read_intervals", "%+#128",
                "-show_entries", "packet=pts_time", "-of", "csv=p=0", path
            ]
            :
            [
                "-v", "error", "-select_streams", "V:0", "-read_intervals", "%+#128", "-show_frames",
                "-show_entries", "frame=best_effort_timestamp_time", "-of", "csv=p=0", path
            ];

    public async Task<double> MeasureAsync(string path, double? declaredStart, CancellationToken cancellationToken,
        bool generatedPresentationTimes = true)
    {
        var result = await BoundedToolProcess.RunAsync(ffprobeCommand, Arguments(path, generatedPresentationTimes), cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("The source's initial timestamp probe failed: " + result.Error);
        return Calculate(declaredStart, result.Output);
    }
}
