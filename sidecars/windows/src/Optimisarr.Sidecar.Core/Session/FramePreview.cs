using System.Diagnostics;
using System.Globalization;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>A single, small frame from the already-downloaded local source.</summary>
public interface IFramePreviewExtractor
{
    Task<byte[]?> ExtractAsync(string ffmpeg, string source, double seconds, CancellationToken cancellationToken);
}

public sealed class FfmpegFramePreviewExtractor : IFramePreviewExtractor
{
    public static IReadOnlyList<string> Arguments(string source, double seconds) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin", "-max_alloc", "67108864",
        "-threads", "1", "-ss", Math.Clamp(seconds, 0, 7 * 24 * 3600).ToString("0.00", CultureInfo.InvariantCulture),
        "-i", source, "-map", "0:V:0", "-an", "-sn", "-dn", "-frames:v", "1",
        "-vf", "scale=160:-2", "-threads:v", "1", "-q:v", "7", "-c:v", "mjpeg",
        "-f", "image2pipe", "pipe:1"
    ];

    public async Task<byte[]?> ExtractAsync(string ffmpeg, string source, double seconds, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(seconds) || !File.Exists(source)) return null;
        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in Arguments(source, seconds)) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        Task? errorDrain = null;
        try
        {
            if (!process.Start()) return null;
            // FFmpeg may report malformed media at length. Drain stderr concurrently so its pipe
            // cannot stall extraction; stdout is the only data sent to the monitor.
            errorDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            using var output = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var count = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token);
                if (count == 0) break;
                if (output.Length + count > MonitorProtocol.MaximumPreviewBytes) return null;
                output.Write(buffer, 0, count);
            }
            await process.WaitForExitAsync(timeout.Token);
            await errorDrain;
            return process.ExitCode == 0 ? output.ToArray() : null;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
                }
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
            if (errorDrain is not null)
            {
                try { await errorDrain.WaitAsync(TimeSpan.FromSeconds(1)); }
                catch (Exception error) when (error is OperationCanceledException or IOException or TimeoutException) { }
            }
        }
    }
}

/// <summary>Best-effort, one-at-a-time samples. A preview never participates in the job result.</summary>
public sealed class JobPreviewSampler(
    string ffmpeg,
    IFramePreviewExtractor extractor,
    Func<bool> wanted,
    Action<byte[]> publish,
    Func<DateTimeOffset>? now = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private DateTimeOffset _last;

    public async Task TrySampleAsync(string source, double seconds, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || !wanted() || !double.IsFinite(seconds) || !_gate.Wait(0)) return;
        try
        {
            var current = _now();
            if (current - _last < TimeSpan.FromSeconds(1.5)) return;
            _last = current;
            var jpeg = await extractor.ExtractAsync(ffmpeg, source, seconds, cancellationToken);
            if (jpeg is { Length: > 0 and <= MonitorProtocol.MaximumPreviewBytes }
                && !cancellationToken.IsCancellationRequested && wanted()) publish(jpeg);
        }
        catch (Exception)
        {
            // Sampling is optional. A decoder error, timeout, or unavailable preview cannot fail
            // the encode or hold its progress callback hostage.
        }
        finally { _gate.Release(); }
    }

    public async Task WaitForIdleAsync()
    {
        if (await _gate.WaitAsync(TimeSpan.FromSeconds(1))) _gate.Release();
    }
}
