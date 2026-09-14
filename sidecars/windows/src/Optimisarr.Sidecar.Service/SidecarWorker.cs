using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Service;

/// <summary>
/// The check-in loop, hosted so Windows can start and stop it.
///
/// <para>The session itself owns every decision about what to do with a refusal; this exists only
/// to carry it, to write what it says somewhere a headless machine can be read afterwards, and to
/// stop the service when there is nothing left worth trying.</para>
/// </summary>
public sealed class SidecarWorker(
    SidecarSession session,
    IHostApplicationLifetime lifetime,
    ILogger<SidecarWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Optimisarr sidecar starting ({Version})", SidecarBuild.Version);

        await session.RunAsync(stoppingToken);

        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        // The loop only returns on its own when carrying on is pointless: nothing paired, or a
        // credential the server has thrown away. A service that stayed "Running" in that state
        // would look healthy to anyone checking, which is worse than stopping.
        switch (session.Status.State)
        {
            case SidecarState.Unpaired:
                logger.LogWarning(
                    "Not paired, so there is nothing to check in to. Pair with: "
                    + "Optimisarr.Sidecar.Service.exe --pair <server address>  (code on stdin)");
                break;

            case SidecarState.Stopped:
                logger.LogError("Stopping: {Detail}", session.Status.Detail);
                break;
        }

        lifetime.StopApplication();
    }
}
