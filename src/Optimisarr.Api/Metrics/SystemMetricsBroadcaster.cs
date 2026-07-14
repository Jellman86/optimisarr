using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Optimisarr.Api.Realtime;
using Optimisarr.Core.Metrics;

namespace Optimisarr.Api.Metrics;

/// <summary>
/// Live CPU/GPU telemetry pushed to clients (the Queue detail view's graph) while ffmpeg is
/// running. Everything here is sampled with unprivileged reads only — <c>/proc/stat</c> for CPU,
/// and for the GPU the per-process DRM fdinfo of our own ffmpeg children, falling back to the
/// AMD <c>gpu_busy_percent</c> sysfs node or an <c>nvidia-smi</c> query. None of these need root,
/// CAP_PERFMON, or the i915 perf interface, so no container privilege or compose change is
/// required; when no source yields data the snapshot reports the GPU as unsupported.
/// </summary>
public sealed class SystemMetricsBroadcaster(
    IHubContext<JobsHub> hub,
    ActiveEncodeRegistry encodes,
    ILogger<SystemMetricsBroadcaster> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(1500);
    private static readonly double NanosPerTimestampTick = 1_000_000_000.0 / Stopwatch.Frequency;

    private CpuSample? _previousCpu;
    private Dictionary<long, IReadOnlyDictionary<string, long>> _previousDrm = new();
    private long _previousDrmTimestamp;
    private bool? _nvidiaAvailable;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                // Keep the CPU baseline current every tick so the first reading after work
                // starts is already meaningful; broadcast while something is encoding or being
                // verified (the VMAF pass is CPU-heavy and worth showing even with no encode).
                var cpuPercent = UpdateCpu();
                if (encodes.Count == 0 && !encodes.VerificationInProgress)
                {
                    _previousDrm.Clear();
                    continue;
                }

                // Verification (VMAF) is CPU-only and registers no encode process, so there are no
                // per-process GPU counters to read; sample the GPU only when an encode is running.
                var gpu = encodes.Count == 0 ? null : SampleGpu(encodes.Pids);
                await hub.Clients.All.SendAsync(
                    "systemMetrics",
                    new
                    {
                        cpuPercent = Math.Round(cpuPercent, 1),
                        gpuSupported = gpu is not null,
                        gpuPercent = gpu is { } g ? Math.Round(g.Percent, 1) : (double?)null,
                        gpuEngine = gpu?.Engine,
                    },
                    stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Telemetry must never take the host down; log once per failing tick and carry on.
                logger.LogDebug(ex, "System metrics sample failed");
            }
        }
    }

    private double UpdateCpu()
    {
        if (!OperatingSystem.IsLinux())
        {
            return 0;
        }

        try
        {
            using var reader = new StreamReader("/proc/stat");
            var current = CpuSample.Parse(reader.ReadLine());
            if (current is null)
            {
                return 0;
            }

            var percent = _previousCpu is { } previous ? CpuSample.Utilisation(previous, current.Value) : 0;
            _previousCpu = current;
            return percent;
        }
        catch
        {
            return 0;
        }
    }

    // Tries each unprivileged source in turn. Order is vendor-neutral: the DRM fdinfo path
    // covers any driver that exposes per-client engine counters (Intel i915/xe and AMD amdgpu),
    // then the AMD sysfs busy node, then an nvidia-smi query for NVIDIA.
    private (double Percent, string? Engine)? SampleGpu(IReadOnlyCollection<int> pids)
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

        var nvidia = SampleNvidia();
        return nvidia is { } nvidiaPercent ? (nvidiaPercent, "GPU") : null;
    }

    // Per-process DRM fdinfo: read the engine busy-nanosecond counters of our own ffmpeg
    // children and diff them per client between samples. New clients contribute nothing until
    // their next sample (no baseline), which avoids a spike when an encode starts. Driver-agnostic
    // — works for any DRM driver that publishes drm-engine-* counters (i915, xe, amdgpu, …).
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
        foreach (var (clientId, engines) in current)
        {
            if (!_previousDrm.TryGetValue(clientId, out var previousEngines))
            {
                continue;
            }

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

    private int? SampleNvidia()
    {
        if (_nvidiaAvailable == false)
        {
            return null;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=utilization.gpu --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                _nvidiaAvailable = false;
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            _nvidiaAvailable = true;
            return GpuValueParsers.ParseNvidiaSmiUtilisation(output);
        }
        catch
        {
            // nvidia-smi absent (the common case on non-NVIDIA hosts): stop trying.
            _nvidiaAvailable = false;
            return null;
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
