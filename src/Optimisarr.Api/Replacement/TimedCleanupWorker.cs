namespace Optimisarr.Api.Replacement;

/// <summary>
/// Applies the shared cleanup retention window every six hours and once at startup. A missed sweep
/// is harmless: failures are logged and the next pass catches the same expired files.
/// </summary>
public sealed class TimedCleanupWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<TimedCleanupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var cleanup = scope.ServiceProvider.GetRequiredService<TimedCleanupService>();
                await cleanup.PurgeExpiredAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Timed retention cleanup failed");
            }

            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
