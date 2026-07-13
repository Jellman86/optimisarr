using System.Diagnostics;

namespace Optimisarr.Core.Verification;

/// <summary>The outcome of attempting a VMAF measurement: scores, or why it could not be measured.</summary>
public sealed record QualityResult(bool Measured, QualityScores? Scores, string? Error)
{
    public static QualityResult Ok(QualityScores scores) => new(true, scores, null);

    public static QualityResult Failed(string error) => new(false, null, error);
}

/// <summary>
/// Measures the perceptual quality of a converted output against its original with
/// FFmpeg's <c>libvmaf</c> filter, writing a JSON log that the pure
/// <see cref="QualityScoreParser"/> turns into scores. The distorted stream is
/// prepared by <see cref="QualityScoreCommandBuilder"/> so resolution, timebase,
/// colour range, model, and HDR-to-SDR handling are deterministic. FFmpeg is
/// invoked through an explicit argument list, never a shell string.
/// </summary>
public sealed class QualityScoreService(string? ffmpegCommand = null)
{
    // VMAF needs an ffmpeg built with libvmaf, which may differ from the transcoding
    // binary; the composition layer can point this at one (e.g. jellyfin-ffmpeg).
    private readonly string _ffmpeg = string.IsNullOrWhiteSpace(ffmpegCommand) ? "ffmpeg" : ffmpegCommand;

    public async Task<QualityResult> MeasureAsync(
        string referencePath,
        string distortedPath,
        QualityMeasurementContext context,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(referencePath))
        {
            return QualityResult.Failed($"Original file does not exist: {referencePath}");
        }
        if (!File.Exists(distortedPath))
        {
            return QualityResult.Failed($"Output file does not exist: {distortedPath}");
        }

        // A unique log path with no special characters keeps the filtergraph valid.
        var logPath = Path.Combine(Path.GetTempPath(), $"optimisarr-vmaf-{Guid.NewGuid():N}.json");
        QualityScoreCommand command;
        try
        {
            // Keep measurement useful without monopolising a small home server. Four
            // libvmaf workers scale well while leaving capacity for the API and disk I/O.
            var threads = Math.Clamp(Environment.ProcessorCount, 1, 4);
            command = QualityScoreCommandBuilder.Build(
                distortedPath, referencePath, logPath, context, threads);
        }
        catch (ArgumentException ex)
        {
            return QualityResult.Failed($"Could not prepare VMAF measurement: {ex.Message}");
        }

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
                return QualityResult.Failed($"Could not run ffmpeg libvmaf: {ex.Message}");
            }

            if (exitCode != 0)
            {
                var message = FirstLine(stderr) ?? $"ffmpeg libvmaf exited with code {exitCode}";
                return QualityResult.Failed(message);
            }

            if (!File.Exists(logPath))
            {
                return QualityResult.Failed("libvmaf produced no log; this ffmpeg build may lack libvmaf support.");
            }

            var json = await File.ReadAllTextAsync(logPath, cancellationToken);
            var scores = QualityScoreParser.Parse(json);
            if (scores is not null)
            {
                scores = scores with
                {
                    ModelVersion = command.ModelVersion,
                    Preprocessing = command.Preprocessing
                };
            }
            return scores is null
                ? QualityResult.Failed("libvmaf log contained no usable VMAF score.")
                : QualityResult.Ok(scores);
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
