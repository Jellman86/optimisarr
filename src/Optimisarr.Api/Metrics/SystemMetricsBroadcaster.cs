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
    private readonly LinuxSystemMetrics _metrics = new();

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
                var cpuPercent = _metrics.ReadCpu() ?? 0;
                if (encodes.Count == 0 && !encodes.VerificationInProgress)
                {
                    _metrics.ResetGpu();
                    continue;
                }

                // Verification (VMAF) is CPU-only and registers no encode process, so there are no
                // per-process GPU counters to read; sample the GPU only when an encode is running.
                var gpu = encodes.Count == 0 ? null : await _metrics.ReadGpuAsync(encodes.Pids, stoppingToken);
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

}
