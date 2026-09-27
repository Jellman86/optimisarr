using System.Diagnostics;
using Optimisarr.Core.Metrics;

namespace Optimisarr.Api.Metrics;

// Shared with the Linux sidecar through a linked source file. Device reads require no
// privileged container or host PID namespace. Null readings mean unavailable, never idle.
public sealed class LinuxSystemMetrics
{
    private static readonly double NanosPerTimestampTick = 1_000_000_000.0 / Stopwatch.Frequency;
    private CpuSample? _previousCpu;
    private Dictionary<long, IReadOnlyDictionary<string, long>> _previousDrm = new();
    private long _previousDrmTimestamp;
    private bool? _nvidiaAvailable;
    public void ResetGpu() => _previousDrm.Clear();

    public double? ReadCpu()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            using var reader = new StreamReader("/proc/stat");
            var current = CpuSample.Parse(reader.ReadLine());
            if (current is null)
            {
                return null;
            }

            var percent = _previousCpu is { } previous ? CpuSample.Utilisation(previous, current.Value) : (double?)null;
            _previousCpu = current;
            return percent;
        }
        catch
        {
            return null;
        }
    }

    // Tries each unprivileged source in turn. Order is vendor-neutral: the DRM fdinfo path
    // covers any driver that exposes per-client engine counters (Intel i915 and AMD amdgpu),
    // then the AMD sysfs busy node, then an nvidia-smi query for NVIDIA.
    public async Task<(double Percent, string? Engine)?> ReadGpuAsync(IReadOnlyCollection<int> pids, CancellationToken token)
    {
        var fromFdinfo = SampleDrmFdinfo(pids);
        if (fromFdinfo is not null)
        {
            return fromFdinfo;
        }

        var amd = SampleAmd();
        if (amd is { } amdPercent)
        {
            return (amdPercent, "GPU");
        }

        var nvidia = await SampleNvidiaAsync(token);
        return nvidia is { } nvidiaPercent ? (nvidiaPercent, "GPU") : null;
    }

    // Per-process DRM fdinfo: read the engine busy-nanosecond counters of our own ffmpeg
    // children and diff them per client between samples. New clients contribute nothing until
    // their next sample (no baseline), which avoids a spike when an encode starts. Driver-agnostic
    // — works for any DRM driver that publishes drm-engine-* counters (i915, amdgpu, …).
    private (double Percent, string? Engine)? SampleDrmFdinfo(IReadOnlyCollection<int> pids)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var current = new Dictionary<long, IReadOnlyDictionary<string, long>>();
        foreach (var pid in pids)
        {
            string[] fds;
            try
            {
                fds = Directory.GetFiles($"/proc/{pid}/fdinfo");
            }
            catch
            {
                continue; // process gone, or its fdinfo not readable
            }

            foreach (var fd in fds)
            {
                string text;
                try
                {
                    text = File.ReadAllText(fd);
                }
                catch
                {
                    continue; // fd closed between listing and reading
                }

                var client = DrmFdinfoParser.ParseClient(text);
                if (client is not null)
                {
                    current[client.ClientId] = client.EngineNanos; // de-dup: one entry per client
                }
            }
        }

        var now = Stopwatch.GetTimestamp();
        if (current.Count == 0)
        {
            _previousDrm = current;
            _previousDrmTimestamp = now;
            return null;
        }

        var deltaByEngine = new Dictionary<string, long>(StringComparer.Ordinal);
        var hasBaseline = false;
        foreach (var (clientId, engines) in current)
        {
            if (!_previousDrm.TryGetValue(clientId, out var previousEngines))
            {
                continue;
            }

            hasBaseline = true;
            foreach (var (engine, nanos) in engines)
            {
                var delta = nanos - previousEngines.GetValueOrDefault(engine);
                if (delta > 0)
                {
                    deltaByEngine[engine] = deltaByEngine.GetValueOrDefault(engine) + delta;
                }
            }
        }

        var elapsedNanos = (now - _previousDrmTimestamp) * NanosPerTimestampTick;
        _previousDrm = current;
        _previousDrmTimestamp = now;

        if (!hasBaseline) return null;
        var (percent, busiestEngine) = DrmEngineUtilisation.Busiest(
            new Dictionary<string, long>(), deltaByEngine, elapsedNanos);
        return (percent, FriendlyEngine(busiestEngine));
    }

    // AMD (and some others) expose overall busy% at /sys/class/drm/card<N>/device/gpu_busy_percent.
    // Scan the cards rather than assuming card0, since an iGPU may take card0 ahead of a dGPU.
    private static int? SampleAmd()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            foreach (var card in Directory.EnumerateDirectories("/sys/class/drm", "card[0-9]*"))
            {
                var path = Path.Combine(card, "device", "gpu_busy_percent");
                if (File.Exists(path))
                {
                    var value = GpuValueParsers.ParseSysfsBusyPercent(File.ReadAllText(path));
                    if (value is not null)
                    {
                        return value;
                    }
                }
            }
        }
        catch
        {
            // sysfs layout differs or is unreadable: treat as unavailable.
        }

        return null;
    }

    private async Task<int?> SampleNvidiaAsync(CancellationToken token)
    {
        if (_nvidiaAvailable == false) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var process = new Process { StartInfo = new ProcessStartInfo("nvidia-smi")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        } };
        process.StartInfo.ArgumentList.Add("--query-gpu=utilization.gpu");
        process.StartInfo.ArgumentList.Add("--format=csv,noheader,nounits");
        try
        {
            if (!process.Start()) { _nvidiaAvailable = false; return null; }
            _nvidiaAvailable = true;
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await error;
            return process.ExitCode == 0 ? GpuValueParsers.ParseNvidiaSmiUtilisation(await output) : null;
        }
        catch (System.ComponentModel.Win32Exception) { _nvidiaAvailable = false; return null; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    // Maps a raw DRM engine name to a friendly label. Covers Intel i915/xe names (render, video,
    // video-enhance, …) and AMD amdgpu names (gfx, enc, dec, …); anything else is title-cased.
    private static string FriendlyEngine(string? engine) => engine switch
    {
        null => "GPU",
        "render" or "gfx" => "Render",
        "video" or "enc" => "Video",
        "video-enhance" => "Video enhance",
        "dec" => "Decode",
        "copy" or "blitter" => "Copy",
        "compute" => "Compute",
        _ => char.ToUpperInvariant(engine[0]) + engine[1..],
    };
}
