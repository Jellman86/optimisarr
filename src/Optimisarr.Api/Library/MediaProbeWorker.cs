using Microsoft.Extensions.DependencyInjection;

namespace Optimisarr.Api.Library;

/// <summary>
/// Probes newly discovered media in the background so a scanned library turns into optimisation
/// candidates without the operator probing each file by hand. Scanning only records that a file
/// exists; candidate evaluation needs its codec, media kind, and dimensions, which come from
/// ffprobe — so without this stage a freshly scanned library (of any type) would sit at
/// "Discovered" and never reach the queue. Probe failures are recorded as
/// <see cref="Optimisarr.Data.MediaFileStatus.ProbeFailed"/> and not retried, so the sweep converges.
/// </summary>
public sealed class MediaProbeWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<MediaProbeWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(20);

    // A fresh scope (and DbContext) is taken per batch, so this also bounds how many entities a
    // single context tracks while draining a large backlog.
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var probed = 0;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var inventory = scope.ServiceProvider.GetRequiredService<LibraryInventoryService>();
                probed = await inventory.ProbePendingAsync(null, BatchSize, stoppingToken);
                if (probed > 0)
                {
                    logger.LogInformation("Probed {Count} newly discovered file(s).", probed);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Media probe sweep failed");
            }

            // A full batch means there is probably more waiting, so drain it promptly; otherwise
            // idle until the next scan adds work.
            if (probed >= BatchSize)
            {
                continue;
            }

            try { await Task.Delay(IdleInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
