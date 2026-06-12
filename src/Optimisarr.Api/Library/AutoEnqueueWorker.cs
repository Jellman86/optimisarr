using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Scheduling;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

/// <summary>
/// Drives per-library automatic optimisation. On a one-minute cadence (and once at
/// startup) it asks the pure <see cref="AutoEnqueueScheduleEvaluator"/> which enabled
/// libraries are due — once per occurrence of each library's daily window — then scans
/// and enqueues those, stamping <see cref="Data.Library.LastAutoEnqueueAt"/> so the run
/// does not repeat within the same window.
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

        var nowLocal = DateTime.Now;
        var due = candidates
            .Where(library => AutoEnqueueScheduleEvaluator.IsDue(
                library.AutoEnqueueEnabled,
                library.AutoEnqueueWindowStart,
                library.AutoEnqueueWindowEnd,
                nowLocal,
                library.LastAutoEnqueueAt?.ToLocalTime().DateTime))
            .ToList();

        if (due.Count == 0)
        {
            return;
        }

        var inventory = scope.ServiceProvider.GetRequiredService<LibraryInventoryService>();
        var enqueue = scope.ServiceProvider.GetRequiredService<JobEnqueueService>();
        var enqueuedAny = false;

        foreach (var library in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await inventory.ScanAsync(library, cancellationToken);
                // Newly discovered files have no probe data yet, and candidate evaluation needs it,
                // so probe this library's pending files before enqueuing — otherwise the very files
                // this scan just found could never be queued in the same run.
                await inventory.ProbePendingAsync(library.Id, int.MaxValue, cancellationToken);
                var result = await enqueue.EnqueueEligibleAsync(library, cancellationToken);

                // Stamp only after a clean run so a transient failure retries next tick
                // rather than being skipped until the next window.
                library.LastAutoEnqueueAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);

                enqueuedAny |= result.Enqueued > 0;
                logger.LogInformation(
                    "Auto-enqueue ran for library {LibraryId} ({Name}): {Enqueued} queued, {AlreadyQueued} already active, {Ineligible} ineligible, {Importing} held for import",
                    library.Id, library.Name, result.Enqueued, result.AlreadyQueued, result.Ineligible, result.Importing);
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
