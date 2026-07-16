using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace Optimisarr.Core.Verification;

/// <summary>
/// Finds the presentation offset between FFmpeg's preceding-keyframe stream-copy reference and
/// the first frame retained by an accurate input seek. Results are cached by file identity because
/// one calibration session prepares several quality levels from each of the same three scenes.
/// </summary>
public sealed class ReferenceFrameAlignmentProbe
{
    private static readonly int[] ProbeDurationsSeconds = [2, 10, 60];
    private readonly ConcurrentDictionary<CacheKey, double> _cache = new();
    private readonly string _ffprobe;

    public ReferenceFrameAlignmentProbe(string? ffprobeCommand = null)
    {
        _ffprobe = string.IsNullOrWhiteSpace(ffprobeCommand) ? "ffprobe" : ffprobeCommand;
    }

    public async Task<double?> MeasureAsync(
        string path,
        int requestedStartSeconds,
        CancellationToken cancellationToken)
    {
        if (requestedStartSeconds <= 0)
        {
            return 0;
        }

        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists)
            {
                return null;
            }
        }
        catch (IOException)
        {
            return null;
        }

        var key = new CacheKey(path, file.Length, file.LastWriteTimeUtc, requestedStartSeconds);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        foreach (var duration in ProbeDurationsSeconds)
        {
            var output = await ProbeAsync(path, requestedStartSeconds, duration, cancellationToken);
            if (output is null)
            {
                continue;
            }

            if (ReferenceFrameAlignmentParser.Parse(output, requestedStartSeconds) is { } offset)
            {
                _cache.TryAdd(key, offset);
                return offset;
            }
        }

        return null;
    }

    private async Task<string?> ProbeAsync(
        string path,
        int requestedStartSeconds,
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = _ffprobe,
                ArgumentList =
                {
                    "-v", "error",
                    "-select_streams", "v:0",
                    "-read_intervals", $"{requestedStartSeconds}%+{durationSeconds}",
                    "-show_frames",
                    "-show_entries", "frame=key_frame,best_effort_timestamp_time",
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

            var stdout = await stdoutTask;
            await stderrTask;
            return process.ExitCode == 0 ? stdout : null;
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
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
            // Best effort while cancellation is already unwinding.
        }
    }

    private sealed record CacheKey(
        string Path,
        long Size,
        DateTime LastWriteTimeUtc,
        int RequestedStartSeconds);
}

/// <summary>Pure parser for the frame list emitted by <see cref="ReferenceFrameAlignmentProbe"/>.</summary>
public static class ReferenceFrameAlignmentParser
{
    public static double? Parse(string csv, double requestedStartSeconds)
    {
        double? precedingKeyframe = null;
        double? firstAccurateFrame = null;

        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length < 2
                || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var keyFrame)
                || !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var timestamp))
            {
                continue;
            }

            if (keyFrame == 1 && timestamp <= requestedStartSeconds)
            {
                precedingKeyframe = timestamp;
            }

            if (firstAccurateFrame is null && timestamp >= requestedStartSeconds)
            {
                firstAccurateFrame = timestamp;
            }
        }

        return precedingKeyframe is { } keyframe && firstAccurateFrame is { } accurate
            ? Math.Max(0, accurate - keyframe)
            : null;
    }
}
