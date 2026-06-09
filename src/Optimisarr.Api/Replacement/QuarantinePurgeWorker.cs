namespace Optimisarr.Api.Replacement;

/// <summary>
/// Runs the quarantine retention policy on a slow cadence (and once at startup).
/// Retention is measured in days, so a periodic sweep is plenty — there is no need
/// to react instantly. Failures are logged and the loop continues; a missed sweep is
/// caught by the next one.
/// </summary>
public sealed class QuarantinePurgeWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<QuarantinePurgeWorker> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var purge = scope.ServiceProvider.GetRequiredService<QuarantinePurgeService>();
                await purge.PurgeExpiredAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Quarantine retention sweep failed");
            }

            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
