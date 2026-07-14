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
/// pure <see cref="ImageSsimParser"/> turns into a score. Both inputs are independently
/// normalised to explicit reference dimensions, timebase, range, and planar RGB/RGBA. This avoids
/// deprecated two-input scaling behavior and makes alpha participate in the score when applicable.
/// FFmpeg is invoked through an explicit argument list, never a shell string.
/// </summary>
public sealed class ImageQualityService(string? ffmpegCommand = null)
{
    private readonly string _ffmpeg = string.IsNullOrWhiteSpace(ffmpegCommand) ? "ffmpeg" : ffmpegCommand;

    public async Task<ImageQualityResult> MeasureAsync(
        string referencePath,
        string distortedPath,
        ImageQualityMeasurementContext context,
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
        if (context.ReferenceWidth <= 0 || context.ReferenceHeight <= 0)
        {
            return ImageQualityResult.Failed("Original image dimensions could not be measured.");
        }

        // A unique log path with no special characters keeps the filtergraph valid.
        var logPath = Path.Combine(Path.GetTempPath(), $"optimisarr-ssim-{Guid.NewGuid():N}.log");
        var command = ImageQualityCommandBuilder.Build(distortedPath, referencePath, logPath, context);

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
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var argument in command.Arguments)
                {
                    process.StartInfo.ArgumentList.Add(argument);
                }

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
