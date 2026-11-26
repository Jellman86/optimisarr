using System.Diagnostics;

namespace Optimisarr.Core.Verification;

/// <summary>
/// Runs an FFmpeg analysis whose answer is printed on stderr, such as <c>cropdetect</c> or
/// <c>silencedetect</c>, or on stdout, such as a <c>hash</c> muxer. Null when FFmpeg will not start
/// or exits with an error, so each caller can fall back to doing nothing.
/// </summary>
internal static class FfmpegStderr
{
    public static async Task<string?> RunAsync(string ffmpeg, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        (await RunCapturedAsync(ffmpeg, arguments, cancellationToken))?.Stderr;

    public static async Task<string?> RunForStdoutAsync(string ffmpeg, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        (await RunCapturedAsync(ffmpeg, arguments, cancellationToken))?.Stdout;

    // Mirrors ImageQualityService: argument list never a shell string, both streams captured,
    // cancellation kills the process rather than abandoning it.
    private static async Task<(string Stdout, string Stderr)?> RunCapturedAsync(
        string ffmpeg, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
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
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }
                throw;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return process.ExitCode == 0 ? (stdout, stderr) : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
