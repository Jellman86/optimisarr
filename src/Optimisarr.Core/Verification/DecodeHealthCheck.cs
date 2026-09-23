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
/// <c>-v error</c> FFmpeg prints one line per corrupt frame or packet read error.
/// A clean file is decoded in full; a candidate with many errors is stopped once
/// the failure is conclusive, so corrupt media cannot keep the host busy indefinitely.
/// FFmpeg is invoked through an explicit argument list, never a shell string.
/// </summary>
public sealed class DecodeHealthCheck
{
    public const int MaximumUsefulDecodeErrors = 100;
    private static readonly TimeSpan DecodeProgressTimeout = TimeSpan.FromMinutes(5);
    private readonly string _ffmpeg;

    public DecodeHealthCheck(string? ffmpegCommand = null)
    {
        _ffmpeg = string.IsNullOrWhiteSpace(ffmpegCommand) ? "ffmpeg" : ffmpegCommand;
    }

    public async Task<DecodeHealthResult> CheckAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return DecodeHealthResult.Unhealthy($"File does not exist: {path}");
        }

        DecodeIntegrity integrity;
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
                    "-nostats",
                    "-progress", "pipe:1",
                    "-threads", "4",
                    "-i", path,
                    "-map", "0:V?", "-map", "0:a?",
                    "-f", "null",
                    "-"
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();
            using var stalled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stalled.CancelAfter(DecodeProgressTimeout);
            var stdoutTask = ReadProgressAsync(process.StandardOutput, stalled, stalled.Token);
            var stderrTask = ReadDecodeErrorsAsync(process.StandardError, process, stalled.Token);

            try
            {
                await process.WaitForExitAsync(stalled.Token);
                await stdoutTask;
                integrity = await stderrTask;
            }
            catch (OperationCanceledException)
            {
                KillQuietly(process);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                return DecodeHealthResult.Unhealthy(
                    $"FFmpeg made no decode progress for {DecodeProgressTimeout.TotalMinutes:0} minutes; verification stopped to protect the host.");
            }
            exitCode = process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return DecodeHealthResult.Unhealthy(ex.Message);
        }

        // A non-zero exit is a hard decode failure; otherwise the stderr lines (at
        // -v error, one per corrupt frame/packet) are the decode-error tally.
        if (exitCode != 0 && integrity.ErrorCount == 0)
        {
            return DecodeHealthResult.Unhealthy($"ffmpeg decode exited with code {exitCode}");
        }

        return integrity.ErrorCount == 0
            ? DecodeHealthResult.Ok
            : DecodeHealthResult.Unhealthy(
                integrity.ErrorCount >= MaximumUsefulDecodeErrors
                    ? $"At least {integrity.ErrorCount} decode errors; stopped checking this corrupt candidate. First: {integrity.FirstError}"
                    : integrity.FirstError!,
                integrity.ErrorCount);
    }

    private static async Task ReadProgressAsync(
        StreamReader reader,
        CancellationTokenSource stalled,
        CancellationToken cancellationToken)
    {
        TimeSpan? lastMediaTime = null;
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (!line.StartsWith("out_time=", StringComparison.Ordinal)
                || !TimeSpan.TryParse(line.AsSpan("out_time=".Length),
                    System.Globalization.CultureInfo.InvariantCulture, out var mediaTime)
                || lastMediaTime is { } previous && mediaTime <= previous)
            {
                continue;
            }

            lastMediaTime = mediaTime;
            stalled.CancelAfter(DecodeProgressTimeout);
        }
    }

    private static async Task<DecodeIntegrity> ReadDecodeErrorsAsync(
        StreamReader reader,
        Process process,
        CancellationToken cancellationToken)
    {
        var errors = new DecodeIntegrityAccumulator();
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            errors.AddLine(line);
            if (errors.Result.ErrorCount >= MaximumUsefulDecodeErrors)
            {
                KillQuietly(process);
                break;
            }
        }

        return errors.Result;
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
