using System.Diagnostics;

namespace Optimisarr.Core.Verification;

/// <summary>The outcome of an EBU R128 loudness measurement.</summary>
public sealed record LoudnessResult(bool Measured, double? IntegratedLufs, string? Error)
{
    public static LoudnessResult Ok(double lufs) => new(true, lufs, null);

    public static LoudnessResult Failed(string error) => new(false, null, error);
}

/// <summary>
/// Measures a file's integrated loudness (EBU R128) by decoding it through FFmpeg's
/// <c>ebur128</c> filter and parsing the summary with the pure
/// <see cref="LoudnessParser"/>. The summary prints at info level, so the decode is
/// run at <c>-v info</c> with stats suppressed. FFmpeg is invoked through an
/// explicit argument list, never a shell string.
/// </summary>
public sealed class LoudnessService
{
    private const string FfmpegCommand = "ffmpeg";

    public async Task<LoudnessResult> MeasureAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return LoudnessResult.Failed($"File does not exist: {path}");
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
                    "-hide_banner",
                    "-nostats",
                    "-v", "info",
                    "-i", path,
                    "-af", "ebur128",
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
            return LoudnessResult.Failed($"Could not run ffmpeg ebur128: {ex.Message}");
        }

        if (exitCode != 0)
        {
            return LoudnessResult.Failed($"ffmpeg ebur128 exited with code {exitCode}");
        }

        var lufs = LoudnessParser.ParseIntegratedLufs(stderr);
        return lufs is null
            ? LoudnessResult.Failed("ebur128 produced no integrated-loudness summary.")
            : LoudnessResult.Ok(lufs.Value);
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
