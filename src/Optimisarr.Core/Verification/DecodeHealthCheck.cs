using System.Diagnostics;

namespace Optimisarr.Core.Verification;

public sealed record DecodeHealthResult(bool Healthy, string? Error, int ErrorCount = 0)
{
    public static DecodeHealthResult Ok { get; } = new(true, null, 0);

    public static DecodeHealthResult Unhealthy(string error, int errorCount = 1) => new(false, error, errorCount);
}

/// <summary>
/// Runs a full software decode of a file and reports how many decode errors FFmpeg
/// hit. <c>-f null -</c> decodes every frame without writing an output, and at
/// <c>-v error</c> FFmpeg prints one line per corrupt frame or packet read error;
/// the pure <see cref="DecodeIntegrityParser"/> tallies them so a clean file scores
/// zero and a damaged one reports the true count across the whole file (rather than
/// stopping at the first error). FFmpeg is invoked through an explicit argument
/// list, never a shell string.
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
                    "-nostdin",
                    "-v", "error",
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

        // A non-zero exit is a hard decode failure; otherwise the stderr lines (at
        // -v error, one per corrupt frame/packet) are the decode-error tally.
        var integrity = DecodeIntegrityParser.Parse(stderr);
        if (exitCode != 0 && integrity.ErrorCount == 0)
        {
            return DecodeHealthResult.Unhealthy($"ffmpeg decode exited with code {exitCode}");
        }

        return integrity.ErrorCount == 0
            ? DecodeHealthResult.Ok
            : DecodeHealthResult.Unhealthy(integrity.FirstError!, integrity.ErrorCount);
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
}
