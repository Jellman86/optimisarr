using System.Diagnostics;

namespace Optimisarr.Core.Verification;

public sealed record DecodeHealthResult(bool Healthy, string? Error)
{
    public static DecodeHealthResult Ok { get; } = new(true, null);

    public static DecodeHealthResult Unhealthy(string error) => new(false, error);
}

/// <summary>
/// Runs a full software decode of a file and reports whether FFmpeg hit any
/// error. <c>-xerror</c> makes FFmpeg exit non-zero on the first decode error,
/// and <c>-f null -</c> decodes every frame without writing an output. FFmpeg is
/// invoked through an explicit argument list, never a shell string.
/// </summary>
public sealed class DecodeHealthCheck
{
    private const string FfmpegCommand = "ffmpeg";

    public async Task<DecodeHealthResult> CheckAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return DecodeHealthResult.Unhealthy($"File does not exist: {path}");
        }

        string stderr;
        int exitCode;

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = FfmpegCommand,
                ArgumentList =
                {
                    "-v", "error",
                    "-xerror",
                    "-i", path,
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
            return DecodeHealthResult.Unhealthy(ex.Message);
        }

        // With "-v error", any line on stderr is a real decode problem; combined
        // with the non-zero exit from -xerror this is an unambiguous failure.
        if (exitCode != 0 || !string.IsNullOrWhiteSpace(stderr))
        {
            var message = FirstLine(stderr) ?? $"ffmpeg decode exited with code {exitCode}";
            return DecodeHealthResult.Unhealthy(message);
        }

        return DecodeHealthResult.Ok;
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

    private static string? FirstLine(string value) =>
        value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
}
