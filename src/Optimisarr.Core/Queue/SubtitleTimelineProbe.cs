using System.Diagnostics;

namespace Optimisarr.Core.Queue;

/// <summary>Reads only subtitle packet timing, without decoding or collecting cue text.</summary>
public sealed class SubtitleTimelineProbe(string ffprobe)
{
    public async Task<bool> RequiresMatroskaAsync(
        string path, IReadOnlyCollection<int> keptStreamIndexes, CancellationToken cancellationToken)
    {
        if (keptStreamIndexes.Count == 0) return false;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(ffprobe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        foreach (var argument in new[] { "-v", "error", "-select_streams", "s", "-show_entries",
                     "packet=stream_index,pts_time,duration_time", "-of", "csv=p=0", path })
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        using var stop = cancellationToken.Register(() =>
        {
            Stop();
        });
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            var scan = new SubtitleTimelineAccumulator(keptStreamIndexes);
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                scan.AddLine(line);
                if (!scan.ProvedIncompatible) continue;
                Stop();
                await process.WaitForExitAsync(cancellationToken);
                await errors;
                cancellationToken.ThrowIfCancellationRequested();
                return true;
            }
            await process.WaitForExitAsync(cancellationToken);
            await errors;
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Source subtitle timing could not be probed safely before encoding.");
            return scan.RequiresMatroska;
        }
        finally
        {
            Stop();
        }

        void Stop()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
    }
}
