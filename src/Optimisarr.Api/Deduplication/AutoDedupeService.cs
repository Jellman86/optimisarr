using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Core.Deduplication;
using Optimisarr.Data;

namespace Optimisarr.Api.Deduplication;

/// <summary>
/// Runs the daily duplicate cleanup for each library that asked for it: the same scan and the same cleanup
/// a person starts from the exact copies page, one library at a time. It moves less than that page can, never
/// more: only copies inside the library that asked, only from the report its own scan produced, and never content
/// someone chose a keeper for or ignored in another report. Before each group it checks again that the library
/// still asks and that dry-run mode is off. Moved copies wait in quarantine until the retention period ends.
/// </summary>
public sealed class AutoDedupeService(
    IServiceScopeFactory scopes,
    ExactDuplicateCoordinator scans,
    DedupeCleanupRunner cleanup,
    TimeProvider clock,
    ILogger<AutoDedupeService> logger,
    AutoDedupeTimings? timings = null)
{
    private readonly AutoDedupeTimings _timings = timings ?? new AutoDedupeTimings();

    private enum Turn { Done, Busy }

    public async Task RunDueAsync(CancellationToken token)
    {
        List<int> candidates;
        await using (var scope = scopes.CreateAsyncScope())
        {
            candidates = await scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().Libraries.AsNoTracking()
                .Where(library => library.Enabled && library.AutoDedupeEnabled)
                .OrderBy(library => library.Id)
                .Select(library => library.Id)
                .ToListAsync(token);
        }

        foreach (var libraryId in candidates)
        {
            token.ThrowIfCancellationRequested();
            // An earlier library can take hours, so each one is checked, and timed, when its turn comes.
            var startedAt = clock.GetUtcNow();
            try
            {
                if (await RunAsync(libraryId, startedAt, token) == Turn.Busy) return;
            }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            {
                logger.LogError(error, "Automatic duplicate cleanup of library {LibraryId} failed", libraryId);
                await RecordAsync(libraryId, startedAt, AutoDedupeOutcome.ScanFailed, 0, 0, token);
            }
        }
    }

    private async Task<Turn> RunAsync(int libraryId, DateTimeOffset startedAt, CancellationToken token)
    {
        if (await AllowedAsync(libraryId, token) is not { } allowed || !AutoDedupeSchedule.IsDue(allowed.LastRunAt, startedAt)) return Turn.Done;
        if (allowed.DryRun)
        {
            await RecordAsync(libraryId, startedAt, AutoDedupeOutcome.DryRun, 0, 0, token);
            return Turn.Done;
        }
        if (cleanup.IsRunning) return Turn.Busy;

        long scanId;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var library = await db.Libraries.AsNoTracking().SingleAsync(l => l.Id == libraryId, token);
            // The scan replaces this library's report, so it keeps that report's libraries, and the keepers saved
            // for it, and starts only if nobody has scanned again since.
            var last = await db.ExactDuplicateScans.AsNoTracking().Where(scan => scan.LibraryId == libraryId)
                .Select(scan => (long?)scan.Id).SingleOrDefaultAsync(token);
            var lastScope = last is { } lastId
                ? await db.ExactDuplicateScanLibraries.AsNoTracking().Where(l => l.ScanId == lastId).Select(l => l.LibraryId).ToListAsync(token)
                : [];
            var existing = (await db.Libraries.AsNoTracking().Select(l => l.Id).ToListAsync(token)).ToHashSet();
            var ids = AutoDedupeSchedule.Scope(libraryId, lastScope, existing);
            var selected = await db.Libraries.AsNoTracking().Where(l => ids.Contains(l.Id)).OrderBy(l => l.Id)
                .Select(l => new ExactDuplicateLibrary(l.Id, l.Name, l.Path, l.MediaType.ToString())).ToListAsync(token);
            var files = await ExactDuplicateInputs.ReadAsync(db, ids, token);
            if (files.Count > ExactDuplicateInputs.MaximumCandidates)
            {
                await RecordAsync(libraryId, startedAt, AutoDedupeOutcome.TooManyFiles, 0, 0, token);
                return Turn.Done;
            }
            if (await scans.TryStartScanAsync(libraryId, library.Path, files, false, token, selected,
                    new ExactDuplicateScanExpectation(last)) is not { } started) return Turn.Busy;
            scanId = started;
        }

        if (!await ScanFinishedAsync(libraryId, scanId, token))
        {
            await RecordAsync(libraryId, startedAt, AutoDedupeOutcome.ScanFailed, 0, 0, token);
            return Turn.Done;
        }

        var limits = new DedupeCleanupLimits(scanId, libraryId,
            async check => await AllowedAsync(libraryId, check) is { DryRun: false });
        var launch = await cleanup.StartAsync(libraryId, token, limits);
        switch (launch.Start)
        {
            case DedupeCleanupStart.AlreadyRunning:
                return Turn.Busy;
            case DedupeCleanupStart.NotReady when await scans.ReadStateAsync(scanId, token) is null:
                // Someone scanned again after this scan finished; their report is theirs to clean.
                return Turn.Busy;
            case DedupeCleanupStart.NotReady:
                await RecordAsync(libraryId, startedAt, AutoDedupeOutcome.ScanFailed, 0, 0, token);
                return Turn.Done;
            case DedupeCleanupStart.NothingToDo:
                await RecordAsync(libraryId, startedAt,
                    launch.ManagersAnswered ? AutoDedupeOutcome.NothingToClean : AutoDedupeOutcome.ManagerUnavailable, 0, 0, token);
                return Turn.Done;
            case DedupeCleanupStart.NotAllowed:
                await RecordAsync(libraryId, startedAt, await StopReasonAsync(libraryId, token), 0, 0, token);
                return Turn.Done;
        }

        var status = await launch.Finished!.WaitAsync(token);
        var outcome = status.Interrupted ? await StopReasonAsync(libraryId, token)
            : status.Failures.Count == 0 ? AutoDedupeOutcome.Cleaned
            : status.Moved > 0 ? AutoDedupeOutcome.PartlyCleaned : AutoDedupeOutcome.CleanupFailed;
        await RecordAsync(libraryId, startedAt, outcome, status.Moved, status.BytesHeld, token);
        logger.LogInformation("Automatic duplicate cleanup of library {LibraryId} moved {Moved} copies ({Bytes} bytes) into quarantine",
            libraryId, status.Moved, status.BytesHeld);
        return Turn.Done;
    }

    /// <summary>
    /// True once the scan completed. A scan that never leaves the queue, or stops making progress, is stopped,
    /// so one stuck scan cannot hold up every later library.
    /// </summary>
    private async Task<bool> ScanFinishedAsync(int libraryId, long scanId, CancellationToken token)
    {
        var queuedSince = clock.GetUtcNow();
        var lastChange = queuedSince;
        ExactDuplicateProgress? lastProgress = null;
        while (scans.Active(scanId) is { } live)
        {
            var now = clock.GetUtcNow();
            if (live.Progress != lastProgress)
            {
                lastProgress = live.Progress;
                lastChange = now;
            }
            var stuck = live.Status == "Queued" ? now - queuedSince >= _timings.QueuedLimit : now - lastChange >= _timings.StalledLimit;
            if (stuck)
            {
                logger.LogWarning("Automatic duplicate scan {ScanId} of library {LibraryId} stopped making progress; stopping it", scanId, libraryId);
                // A scan that never started can be withdrawn at once. A running one is only asked to stop, and keeps
                // its place until it has, so later libraries wait for a later tick rather than run beside it.
                if (live.Status != "Queued" || !await scans.WithdrawQueuedAsync(scanId, token)) scans.Cancel(libraryId, scanId);
                return false;
            }
            await Task.Delay(_timings.Poll, clock, token);
        }
        return await scans.ReadStateAsync(scanId, token) == ExactDuplicateScanState.Completed;
    }

    private sealed record Allowance(DateTimeOffset? LastRunAt, bool DryRun);

    /// <summary>Why a run that was no longer allowed stopped: dry-run mode, or the setting turned off.</summary>
    private async Task<AutoDedupeOutcome> StopReasonAsync(int libraryId, CancellationToken token) =>
        await AllowedAsync(libraryId, token) is { DryRun: true } ? AutoDedupeOutcome.DryRun : AutoDedupeOutcome.Stopped;

    /// <summary>Null when the library no longer asks for automatic cleanup or cannot have it.</summary>
    private async Task<Allowance?> AllowedAsync(int libraryId, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var library = await db.Libraries.AsNoTracking().SingleOrDefaultAsync(l => l.Id == libraryId, token);
        if (library is not { Enabled: true, AutoDedupeEnabled: true } || !AutoDedupeSchedule.Supports(library.MediaType)) return null;
        return new Allowance(library.LastAutoDedupeAt, (await new SettingsStore(db).GetQueueSettingsAsync(token)).DryRunMode);
    }

    private async Task RecordAsync(int libraryId, DateTimeOffset startedAt, AutoDedupeOutcome outcome, int moved, long bytes, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await db.Libraries.Where(l => l.Id == libraryId).ExecuteUpdateAsync(set => set
            .SetProperty(l => l.LastAutoDedupeAt, startedAt)
            .SetProperty(l => l.LastAutoDedupeOutcome, outcome)
            .SetProperty(l => l.LastAutoDedupeMoved, moved)
            .SetProperty(l => l.LastAutoDedupeBytes, bytes), token);
    }
}

/// <summary>How often a run checks on its scan, and how long a scan may sit queued or without progress.</summary>
public sealed record AutoDedupeTimings
{
    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan QueuedLimit { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan StalledLimit { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>Checks every quarter hour for a library whose daily duplicate cleanup is due.</summary>
public sealed class AutoDedupeWorker(AutoDedupeService service, ILogger<AutoDedupeWorker> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await service.RunDueAsync(stoppingToken);
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
