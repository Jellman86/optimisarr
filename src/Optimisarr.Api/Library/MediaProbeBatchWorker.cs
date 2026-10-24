using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

/// <summary>
/// Works through the probe batches the operator asked for. A batch inspects files again with
/// ffprobe, which is a process per file and a database write per file, so the files are inspected
/// one at a time: the work stays inside the server's single-writer discipline, and one slow file
/// delays only its own batch. The operator watches the batch through the progress endpoint, so the
/// work never has to sit inside one long request.
/// </summary>
public sealed class MediaProbeBatchWorker(
    IServiceScopeFactory scopeFactory,
    MediaProbeBatches batches,
    ILogger<MediaProbeBatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            MediaProbeBatches.Batch? batch;
            try
            {
                batch = await batches.TakeAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (batch is null)
            {
                continue;
            }

            try
            {
                await RunAsync(batch, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                batch.Finish(MediaProbeBatchState.Cancelled);
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Probe batch {BatchId} failed.", batch.Id);
                batch.Finish(MediaProbeBatchState.Failed);
            }
        }
    }

    private async Task RunAsync(MediaProbeBatches.Batch batch, CancellationToken stoppingToken)
    {
        batch.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, batch.Token);
        timeout.CancelAfter(MediaProbeBatches.BatchTimeout);

        foreach (var mediaFileId in batch.MediaFileIds)
        {
            if (timeout.IsCancellationRequested)
            {
                batch.Finish(batch.Token.IsCancellationRequested
                    ? MediaProbeBatchState.Cancelled
                    : MediaProbeBatchState.Failed);
                return;
            }

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var inventory = scope.ServiceProvider.GetRequiredService<LibraryInventoryService>();
                var file = await inventory.ProbeAsync(mediaFileId, timeout.Token);

                if (file is null)
                {
                    batch.Record(mediaFileId, MediaProbeOutcome.Skipped, $"No media file with id {mediaFileId}.");
                }
                else if (file.Status == MediaFileStatus.ProbeFailed)
                {
                    batch.Record(mediaFileId, MediaProbeOutcome.Failed, file.ProbeError ?? "The file could not be inspected.");
                }
                else
                {
                    batch.Record(mediaFileId, MediaProbeOutcome.Succeeded, null);
                }
            }
            catch (OperationCanceledException)
            {
                batch.Finish(MediaProbeBatchState.Cancelled);
                return;
            }
            catch (Exception ex)
            {
                // One file that cannot be inspected must not lose the rest of the batch: the failure
                // is recorded against that file and the batch carries on.
                logger.LogWarning(ex, "Probing media file {MediaFileId} failed.", mediaFileId);
                batch.Record(mediaFileId, MediaProbeOutcome.Failed, ex.Message);
            }
        }

        batch.Finish(MediaProbeBatchState.Completed);
    }
}
