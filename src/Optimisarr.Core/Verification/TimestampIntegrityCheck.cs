using System.Diagnostics;

namespace Optimisarr.Core.Verification;

/// <summary>The outcome of reading a file's video packet timestamps.</summary>
/// <param name="Measured">True when ffprobe returned a packet timestamp stream to judge.</param>
/// <param name="NonMonotonicCount">How many packets stepped backward in decode order.</param>
/// <param name="FirstRegressionDetail">A description of the first backward step, or null.</param>
/// <param name="LastPresentationSeconds">The latest presentation time, i.e. where the video ends, or null.</param>
public sealed record TimestampCheckResult(
    bool Measured,
    int NonMonotonicCount,
    string? FirstRegressionDetail,
    double? LastPresentationSeconds)
{
    public static TimestampCheckResult NotMeasured { get; } = new(false, 0, null, null);
}

/// <summary>
/// Reads the output's video packet timestamps with ffprobe and, via the pure
/// <see cref="PacketTimestampParser"/>, tallies any decode timestamp that steps backward
/// and tracks the latest presentation time (where the video actually ends). This is a
/// metadata-only read (<c>-show_entries packet=pts_time,dts_time</c>), not a decode, so
/// it is cheap relative to the full-decode health check, and one pass feeds both the
/// monotonicity and truncated-tail gates. ffprobe is invoked through an explicit argument
/// list, never a shell string, and a probe that yields no timestamps is reported as
/// not-measured so the gates simply abstain rather than blocking on missing evidence.
/// </summary>
public sealed class TimestampIntegrityCheck
{
    private const string FfprobeCommand = "ffprobe";

    public async Task<TimestampCheckResult> CheckAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return TimestampCheckResult.NotMeasured;
        }

        string stdout;
        int exitCode;

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = FfprobeCommand,
                ArgumentList =
                {
                    "-v", "error",
                    "-select_streams", "v:0",
                    "-show_entries", "packet=pts_time,dts_time",
                    "-of", "csv=p=0",
                    path
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

            stdout = await stdoutTask;
            await stderrTask;
            exitCode = process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return TimestampCheckResult.NotMeasured;
        }

        var integrity = PacketTimestampParser.Parse(stdout);

        // No readable timestamps (probe failed or the stream carries none) means we have
        // no evidence to judge, so abstain rather than fail the output.
        if (exitCode != 0 || integrity.TimestampCount == 0)
        {
            return TimestampCheckResult.NotMeasured;
        }

        return new TimestampCheckResult(
            true,
            integrity.NonMonotonicCount,
            integrity.FirstRegressionDetail,
            integrity.LastPresentationSeconds);
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
