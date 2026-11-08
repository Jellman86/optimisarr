using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Core.Deduplication;
using Optimisarr.Data;

namespace Optimisarr.Api.Deduplication;

/// <summary>
/// Runs the daily duplicate cleanup for each library that asked for it: the same scan and the same cleanup
/// a person starts from the exact copies page, one library at a time. It decides nothing that page could not:
/// every copy that cannot safely move is left alone, and moved copies wait in quarantine, where they can be
/// restored until the retention period ends. Nothing runs in dry-run mode.
/// </summary>
public sealed class AutoDedupeService(
    IServiceScopeFactory scopes,
    ExactDuplicateCoordinator scans,
    DedupeCleanupRunner cleanup,
    ILogger<AutoDedupeService> logger,
    TimeSpan? pollInterval = null)
{
    private readonly TimeSpan _poll = pollInterval ?? TimeSpan.FromSeconds(5);

    public async Task RunDueAsync(DateTimeOffset nowUtc, CancellationToken token)
    {
        List<int> due;
        bool dryRun;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            dryRun = (await new SettingsStore(db).GetQueueSettingsAsync(token)).DryRunMode;
            var libraries = await db.Libraries.AsNoTracking()
                .Where(library => library.Enabled && library.AutoDedupeEnabled)
                .OrderBy(library => library.Id)
                .ToListAsync(token);
            due = [.. libraries
                .Where(library => AutoDedupeSchedule.Supports(library.MediaType) && AutoDedupeSchedule.IsDue(library.LastAutoDedupeAt, nowUtc))
                .Select(library => library.Id)];
        }

        foreach (var libraryId in due)
        {
            token.ThrowIfCancellationRequested();
            if (dryRun)
            {
                await RecordAsync(libraryId, nowUtc, AutoDedupeOutcome.DryRun, 0, 0, token);
                continue;
            }
            try
            {
                if (!await RunAsync(libraryId, nowUtc, token)) return;
            }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            {
                logger.LogError(error, "Automatic duplicate cleanup of library {LibraryId} failed", libraryId);
                await RecordAsync(libraryId, nowUtc, AutoDedupeOutcome.ScanFailed, 0, 0, token);
            }
        }
    }

    /// <summary>False when a scan or cleanup someone else started is still running, to try again later.</summary>
    private async Task<bool> RunAsync(int libraryId, DateTimeOffset nowUtc, CancellationToken token)
    {
        if (cleanup.Status(libraryId).Running) return false;

        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var library = await db.Libraries.AsNoTracking().SingleAsync(l => l.Id == libraryId, token);
            var lastScope = await db.ExactDuplicateScans.AsNoTracking()
                .Where(scan => scan.LibraryId == libraryId)
                .SelectMany(scan => db.ExactDuplicateScanLibraries.Where(l => l.ScanId == scan.Id).Select(l => l.LibraryId))
                .ToListAsync(token);
            var existing = (await db.Libraries.AsNoTracking().Select(l => l.Id).ToListAsync(token)).ToHashSet();
            var ids = AutoDedupeSchedule.Scope(libraryId, lastScope, existing);
            var selected = await db.Libraries.AsNoTracking().Where(l => ids.Contains(l.Id)).OrderBy(l => l.Id)
                .Select(l => new ExactDuplicateLibrary(l.Id, l.Name, l.Path, l.MediaType.ToString())).ToListAsync(token);
            var files = await ExactDuplicateInputs.ReadAsync(db, ids, token);
            if (files.Count > ExactDuplicateInputs.MaximumCandidates)
            {
                await RecordAsync(libraryId, nowUtc, AutoDedupeOutcome.TooManyFiles, 0, 0, token);
                return true;
            }
            if (!await scans.TryStartAsync(libraryId, library.Path, files, false, token, selected)) return false;
        }

        var scanned = await scans.ReadAsync(libraryId, 0, token);
        while (scanned.Status is "Queued" or "Running")
        {
            await Task.Delay(_poll, token);
            scanned = await scans.ReadAsync(libraryId, 0, token);
        }
        if (scanned.Status != "Completed")
        {
            await RecordAsync(libraryId, nowUtc, AutoDedupeOutcome.ScanFailed, 0, 0, token);
            return true;
        }

        switch (await cleanup.TryStartAsync(libraryId, token))
        {
            case DedupeCleanupStart.AlreadyRunning:
                return false;
            case DedupeCleanupStart.NothingToDo:
                var preview = await cleanup.PreviewAsync(libraryId, token);
                await RecordAsync(libraryId, nowUtc, preview.LeftAlone.ContainsKey(nameof(DedupeCleanupRefusal.ManagerUnavailable))
                    ? AutoDedupeOutcome.ManagerUnavailable : AutoDedupeOutcome.NothingToClean, 0, 0, token);
                return true;
            case DedupeCleanupStart.NotReady:
                await RecordAsync(libraryId, nowUtc, AutoDedupeOutcome.ScanFailed, 0, 0, token);
                return true;
        }

        var status = cleanup.Status(libraryId);
        while (status.Running)
        {
            await Task.Delay(_poll, token);
            status = cleanup.Status(libraryId);
        }
        var outcome = status.Failures.Count == 0 ? AutoDedupeOutcome.Cleaned
            : status.Moved > 0 ? AutoDedupeOutcome.PartlyCleaned : AutoDedupeOutcome.CleanupFailed;
        await RecordAsync(libraryId, nowUtc, outcome, status.Moved, status.BytesHeld, token);
        logger.LogInformation("Automatic duplicate cleanup of library {LibraryId} moved {Moved} copies ({Bytes} bytes) into quarantine",
            libraryId, status.Moved, status.BytesHeld);
        return true;
    }

    private async Task RecordAsync(int libraryId, DateTimeOffset nowUtc, AutoDedupeOutcome outcome, int moved, long bytes, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await db.Libraries.Where(l => l.Id == libraryId).ExecuteUpdateAsync(set => set
            .SetProperty(l => l.LastAutoDedupeAt, nowUtc)
            .SetProperty(l => l.LastAutoDedupeOutcome, outcome)
            .SetProperty(l => l.LastAutoDedupeMoved, moved)
            .SetProperty(l => l.LastAutoDedupeBytes, bytes), token);
    }
}

/// <summary>Checks every quarter hour for a library whose daily duplicate cleanup is due.</summary>
public sealed class AutoDedupeWorker(AutoDedupeService service, TimeProvider clock, ILogger<AutoDedupeWorker> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await service.RunDueAsync(clock.GetUtcNow(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogError(error, "Automatic duplicate cleanup tick failed");
            }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
