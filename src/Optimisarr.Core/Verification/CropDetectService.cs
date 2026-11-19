using Optimisarr.Core.Queue;

namespace Optimisarr.Core.Verification;

/// <summary>
/// Runs FFmpeg's <c>cropdetect</c> over the same deterministic windows the adaptive VMAF search
/// samples, and turns the reports into one crop through <see cref="CropPlanner"/>.
///
/// Every failure mode resolves to "no crop". A window that cannot be decoded, a window with no
/// report, an ffmpeg that will not start — each means the picture is encoded at its source size,
/// which is what would have happened without this feature. No crop is the only answer that
/// cannot lose picture.
/// </summary>
public sealed class CropDetectService(string ffmpeg)
{
    public async Task<CropRect?> DetectAsync(
        string inputPath,
        PictureSize source,
        IReadOnlyList<VmafWindow> windows,
        CancellationToken cancellationToken)
    {
        if (windows.Count == 0)
        {
            return null;
        }

        var samples = new List<CropRect>(windows.Count);
        foreach (var window in windows)
        {
            var arguments = CropDetectCommandBuilder.Build(
                inputPath, window.StartSeconds ?? 0, window.DurationSeconds);
            var stderr = await RunAsync(arguments, cancellationToken);
            if (stderr is null)
            {
                return null;
            }

            var reports = CropDetectParser.ParseAll(stderr);
            if (reports.Count == 0)
            {
                return null;
            }

            // reset=0 accumulates across the window, so the last report is the whole window's
            // answer — the widest picture any frame in it showed.
            samples.Add(reports[^1]);
        }

        return CropPlanner.Plan(samples, source);
    }

    private Task<string?> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        FfmpegStderr.RunAsync(ffmpeg, arguments, cancellationToken);
}
