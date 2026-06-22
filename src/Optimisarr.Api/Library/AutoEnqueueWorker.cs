using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Scheduling;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

/// <summary>
/// Drives per-library automatic optimisation. On a one-minute cadence it asks the pure
/// <see cref="AutoEnqueueScheduleEvaluator"/> which enabled libraries are inside their window
/// and enqueues each one's eligible candidates. Enqueuing is idempotent, so running every tick
/// while in-window picks up newly-eligible files promptly (rather than only when the window opens).
/// Scanning is handled separately by <see cref="LibraryScanWorker"/> and probing by
/// <see cref="MediaProbeWorker"/>, so this worker no longer scans — it only enqueues.
///
/// This worker only *creates queued jobs*; it never starts them. Execution stays with
/// the single-writer <see cref="QueueDispatcher"/>, so jobs from several libraries
/// enqueuing at once still honour the global concurrency limit and the global
/// processing window. A failure for one library is logged and never blocks the others.
/// </summary>
public sealed class AutoEnqueueWorker(
    IServiceScopeFactory scopeFactory,
    QueueDispatcher dispatcher,
    ILogger<AutoEnqueueWorker> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunDueLibrariesAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Auto-enqueue tick failed");
            }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunDueLibrariesAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();

        var candidates = await db.Libraries
            .Where(library => library.Enabled && library.AutoEnqueueEnabled)
            .ToListAsync(cancellationToken);

        var nowLocal = TimeOnly.FromDateTime(DateTime.Now);
        var due = candidates
            .Where(library => AutoEnqueueScheduleEvaluator.ShouldEnqueueNow(
                library.AutoEnqueueEnabled,
                library.AutoEnqueueWindowStart,
                library.AutoEnqueueWindowEnd,
                nowLocal))
            .ToList();

        if (due.Count == 0)
        {
            return;
        }

        var enqueue = scope.ServiceProvider.GetRequiredService<JobEnqueueService>();
        var enqueuedAny = false;

        foreach (var library in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Scanning and probing run on their own schedules; here we only enqueue what is
                // already eligible. Idempotent: files already queued/optimised are not re-added.
                var result = await enqueue.EnqueueEligibleAsync(library, cancellationToken);

                // Record the last time we actually added work, for the Schedule page; only on a
                // real enqueue so an idle in-window tick doesn't churn the timestamp.
                if (result.Enqueued > 0)
                {
                    library.LastAutoEnqueueAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                    logger.LogInformation(
                        "Auto-enqueue for library {LibraryId} ({Name}): {Enqueued} queued, {AlreadyQueued} already active, {Ineligible} ineligible, {Importing} held for import",
                        library.Id, library.Name, result.Enqueued, result.AlreadyQueued, result.Ineligible, result.Importing);
                }

                enqueuedAny |= result.Enqueued > 0;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Auto-enqueue failed for library {LibraryId} ({Name})", library.Id, library.Name);
            }
        }

        // Let the dispatcher pick up the new work immediately rather than on its next poll.
        if (enqueuedAny)
        {
            dispatcher.Wake();
        }
    }
}
