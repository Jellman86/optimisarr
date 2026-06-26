namespace Optimisarr.Api.Library;

/// <summary>
/// Rescans every enabled library on a global interval (Settings → Library scan interval) so the
/// inventory stays current independent of any per-library auto-enqueue window. Scanning is
/// settling-aware and idempotent — an unchanged library does no work — and newly discovered files
/// are probed by <see cref="MediaProbeWorker"/>. This worker only *discovers* files; it never
/// enqueues or starts jobs. It runs once at startup, then every configured interval.
/// </summary>
public sealed class LibraryScanWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<LibraryScanWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalHours = SettingsStore.DefaultLibraryScanIntervalHours;
            try
            {
                intervalHours = await ScanAllAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled library scan failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(Math.Max(1, intervalHours)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<int> ScanAllAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
        var inventory = scope.ServiceProvider.GetRequiredService<LibraryInventoryService>();

        var interval = (await settings.GetQueueSettingsAsync(cancellationToken)).LibraryScanIntervalHours;

        var summary = await inventory.ScanEnabledAsync(cancellationToken);
        if (summary.Added > 0 || summary.Updated > 0 || summary.Removed > 0)
        {
            logger.LogInformation(
                "Scheduled scan: {Added} new, {Updated} updated, {Removed} removed, {Settling} settling across enabled libraries.",
                summary.Added, summary.Updated, summary.Removed, summary.SkippedUnsettled);
        }

        return interval;
    }
}
