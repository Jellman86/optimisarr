namespace Optimisarr.Api.Library;

/// <summary>
/// Looks again at files a Sonarr/Radarr webhook reported while they were still being written, or while a
/// scan held their library, once they are due. Each one goes through the same intake as a fresh delivery,
/// including its short bounded wait on the library, one at a time through the webhook gate.
/// </summary>
public sealed class ArrWebhookSettlingWorker(
    IServiceScopeFactory scopeFactory,
    ArrWebhookSettlingQueue settling,
    ArrWebhookGate gate,
    TimeProvider clock,
    ILogger<ArrWebhookSettlingWorker> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RevisitDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Revisiting settled webhook files failed");
            }

            try { await Task.Delay(TickInterval, clock, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task RevisitDueAsync(CancellationToken cancellationToken)
    {
        foreach (var file in settling.TakeDue(clock.GetUtcNow()))
        {
            await gate.Slot.WaitAsync(cancellationToken);
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ArrWebhookService>().RevisitAsync(file, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not take in a settled webhook file for connection {ConnectionId}", file.ConnectionId);
            }
            finally
            {
                gate.Slot.Release();
            }
        }
    }
}
