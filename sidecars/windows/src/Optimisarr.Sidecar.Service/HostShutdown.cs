using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Service;

/// <summary>Service-owned intent: closing the tray cannot discard a pending shutdown.</summary>
public sealed class HostShutdown(SidecarSession session, ILogger<HostShutdown> logger) : BackgroundService
{
    private readonly ShutdownCountdown _plan = new();
    private readonly Lock _gate = new();

    public (bool Armed, bool CanCancel, string Detail, int? Seconds) Read()
    {
        lock (_gate) return (_plan.Armed, _plan.CanCancel, _plan.Detail, _plan.SecondsRemaining(DateTimeOffset.UtcNow));
    }

    public void Arm()
    {
        lock (_gate)
        {
            if (_plan.Armed) return;
            if (session.ServerAddress is null) return;
            session.ArmShutdown();
            _plan.Arm();
        }
        logger.LogInformation("Shutdown after current work armed; no new jobs will be claimed.");
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (!_plan.CanCancel) return;
            _plan.Cancel();
            session.CancelShutdown();
        }
        logger.LogInformation("Shutdown after current work canceled.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool begin;
            lock (_gate)
                begin = _plan.Evaluate(DateTimeOffset.UtcNow, session.ShutdownReady,
                    session.ActiveJobCount, session.HasUnacknowledgedResult,
                    session.Status.State == SidecarState.Unreachable);
            if (begin)
            {
                var confirmed = await session.ConfirmShutdownAsync(stoppingToken);
                lock (_gate)
                {
                    if (!confirmed || !session.ShutdownReady)
                    {
                        _plan.Defer("Final server check failed; shutdown is blocked until check-ins recover.");
                        begin = false;
                    }
                    else begin = _plan.Begin();
                }
            }
            if (begin)
            {
                try
                {
                    await RequestShutdownAsync(stoppingToken);
                    logger.LogInformation("Windows accepted the shutdown request.");
                }
                catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    var reason = "Windows could not shut down: " + error.Message;
                    lock (_gate) _plan.Fail(reason);
                    logger.LogError(error, "{Reason}", reason);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private static async Task RequestShutdownAsync(CancellationToken token)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        } };
        process.StartInfo.ArgumentList.Add("/s");
        process.StartInfo.ArgumentList.Add("/t");
        process.StartInfo.ArgumentList.Add("0");
        if (!process.Start()) throw new InvalidOperationException("Windows did not start shutdown.exe.");
        var error = process.StandardError.ReadToEndAsync(token);
        var output = process.StandardOutput.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        await output;
        if (process.ExitCode != 0)
            throw new InvalidOperationException((await error).Trim() is { Length: > 0 } reason
                ? reason : $"shutdown.exe exited with code {process.ExitCode}.");
    }
}
