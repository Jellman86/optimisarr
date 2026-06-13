using System.Diagnostics;

namespace Optimisarr.Core.Verification;

/// <summary>The outcome of attempting an image SSIM measurement: the score, or why it could not be measured.</summary>
public sealed record ImageQualityResult(bool Measured, double? Ssim, string? Error)
{
    public static ImageQualityResult Ok(double ssim) => new(true, ssim, null);

    public static ImageQualityResult Failed(string error) => new(false, null, error);
}

/// <summary>
/// Measures the structural similarity (SSIM) of a re-encoded still against its
/// original with FFmpeg's <c>ssim</c> filter, writing a per-frame stats file that the
/// pure <see cref="ImageSsimParser"/> turns into a score. The output is scaled to the
/// reference with <c>scale2ref</c> so the two pictures compare like-for-like even if a
/// future downscale changes dimensions. FFmpeg is invoked through an explicit argument
/// list, never a shell string.
/// </summary>
public sealed class ImageQualityService(string? ffmpegCommand = null)
{
    private readonly string _ffmpeg = string.IsNullOrWhiteSpace(ffmpegCommand) ? "ffmpeg" : ffmpegCommand;

    public async Task<ImageQualityResult> MeasureAsync(
        string referencePath,
        string distortedPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(referencePath))
        {
            return ImageQualityResult.Failed($"Original file does not exist: {referencePath}");
        }
        if (!File.Exists(distortedPath))
        {
            return ImageQualityResult.Failed($"Output file does not exist: {distortedPath}");
        }

        // A unique log path with no special characters keeps the filtergraph valid.
        var logPath = Path.Combine(Path.GetTempPath(), $"optimisarr-ssim-{Guid.NewGuid():N}.log");
        // Input 0 is the distorted (output) still, input 1 the reference (original).
        // scale2ref scales the distorted picture to the reference so SSIM compares
        // matching dimensions; ssim writes its per-frame All-channel score to the log.
        var filter =
            "[0:v][1:v]scale2ref[dist][ref];" +
            $"[dist][ref]ssim=stats_file={logPath}";

        try
        {
            string stderr;
            int exitCode;

            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = _ffmpeg,
                    ArgumentList =
                    {
                        "-nostdin",
                        "-v", "error",
                        "-i", distortedPath,
                        "-i", referencePath,
                        "-lavfi", filter,
                        "-f", "null",
                        "-"
                    },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                process.Start();

                var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

                try
                {
                    await process.WaitForExitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    KillQuietly(process);
                    throw;
                }

                await stdoutTask;
                stderr = await stderrTask;
                exitCode = process.ExitCode;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return ImageQualityResult.Failed($"Could not run ffmpeg ssim: {ex.Message}");
            }

            if (exitCode != 0)
            {
                var message = FirstLine(stderr) ?? $"ffmpeg ssim exited with code {exitCode}";
                return ImageQualityResult.Failed(message);
            }

            // The summary SSIM line is also on stderr; prefer the stats file but fall back to it.
            var statsText = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath, cancellationToken) : stderr;
            var ssim = ImageSsimParser.Parse(statsText) ?? ImageSsimParser.Parse(stderr);
            return ssim is null
                ? ImageQualityResult.Failed("ssim filter produced no usable SSIM score.")
                : ImageQualityResult.Ok(ssim.Value);
        }
        finally
        {
            DeleteQuietly(logPath);
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Best effort; the process is exiting anyway.
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover temp log is harmless.
        }
    }

    private static string? FirstLine(string value) =>
        value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
}
