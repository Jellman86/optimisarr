using System.Diagnostics;

namespace Optimisarr.Core.Verification;

/// <summary>The outcome of reading a file's video packet timestamps.</summary>
/// <param name="Measured">True when ffprobe returned a packet timestamp stream to judge.</param>
/// <param name="NonMonotonicCount">How many packets stepped backward in decode order.</param>
/// <param name="FirstRegressionDetail">A description of the first backward step, or null.</param>
/// <param name="LastPresentationSeconds">The latest packet endpoint, i.e. where the selected stream ends, or null.</param>
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
/// metadata-only read (<c>-show_entries packet=pts_time,dts_time</c>), not a decode, and
/// one pass feeds both the monotonicity and truncated-tail gates. It still traverses the
/// source, so callers must not repeat it on each queue poll or worker claim. ffprobe uses
/// an explicit argument list, never a shell string. A probe that yields no timestamps is
/// reported as not-measured so the gates abstain rather than blocking on missing evidence.
/// </summary>
public sealed class TimestampIntegrityCheck
{
    // Uppercase V excludes attached pictures and thumbnails, matching MediaProbeService's
    // primary video selection. Lowercase v:0 can measure a single cover-art packet instead.
    public const string MovingPictureStreamSpecifier = "V:0";
    private readonly string _ffprobe;

    public TimestampIntegrityCheck(string? ffprobeCommand = null)
    {
        _ffprobe = string.IsNullOrWhiteSpace(ffprobeCommand) ? "ffprobe" : ffprobeCommand;
    }

    public async Task<TimestampCheckResult> CheckAsync(string path, CancellationToken cancellationToken)
        => await CheckAsync(path, MovingPictureStreamSpecifier, cancellationToken);

    /// <summary>
    /// Reads the primary audio packet endpoint. This deliberately excludes subtitles and secondary
    /// audio tracks so an unusually long ancillary stream cannot make a complete picture look
    /// truncated.
    /// </summary>
    public async Task<TimestampCheckResult> CheckPrimaryAudioAsync(
        string path,
        CancellationToken cancellationToken)
        => await CheckAsync(path, "a:0", cancellationToken);

    private async Task<TimestampCheckResult> CheckAsync(
        string path,
        string streamSpecifier,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return TimestampCheckResult.NotMeasured;
        }

        TimestampIntegrity integrity;
        int exitCode;

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = _ffprobe,
                ArgumentList =
                {
                    "-v", "error",
                    "-select_streams", streamSpecifier,
                    "-show_entries", "packet=pts_time,dts_time,duration_time",
                    "-of", "csv=p=0",
                    path
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();

            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            try
            {
                integrity = await PacketTimestampParser.ParseAsync(process.StandardOutput, cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                KillQuietly(process);
                throw;
            }

            await stderrTask;
            exitCode = process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return TimestampCheckResult.NotMeasured;
        }

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
