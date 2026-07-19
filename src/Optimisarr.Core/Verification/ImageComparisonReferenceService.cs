using System.Diagnostics;

namespace Optimisarr.Core.Verification;

public sealed record ImageComparisonReferenceResult(bool Created, string? Error);

/// <summary>Creates a lossless, browser-compatible PNG view of an image calibration source.</summary>
public sealed class ImageComparisonReferenceService(string? ffmpegCommand = null)
{
    private readonly string _ffmpeg = string.IsNullOrWhiteSpace(ffmpegCommand) ? "ffmpeg" : ffmpegCommand;

    public async Task<ImageComparisonReferenceResult> CreateAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(inputPath))
        {
            return new ImageComparisonReferenceResult(false, $"File does not exist: {inputPath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _ffmpeg,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            foreach (var argument in BuildArguments(inputPath, outputPath))
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                KillQuietly(process);
                throw;
            }

            await stdout;
            var error = await stderr;
            return process.ExitCode == 0 && File.Exists(outputPath)
                ? new ImageComparisonReferenceResult(true, null)
                : new ImageComparisonReferenceResult(
                    false,
                    string.IsNullOrWhiteSpace(error)
                        ? $"ffmpeg exited with code {process.ExitCode}"
                        : error.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault());
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ImageComparisonReferenceResult(false, exception.Message);
        }
    }

    public static IReadOnlyList<string> BuildArguments(string inputPath, string outputPath) =>
        ["-nostdin", "-hide_banner", "-y", "-i", inputPath, "-map", "0:v:0", "-frames:v", "1", "-c:v", "png", outputPath];

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
            // Best effort during cancellation.
        }
    }
}
