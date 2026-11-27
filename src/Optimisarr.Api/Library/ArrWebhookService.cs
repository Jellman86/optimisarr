using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Activity;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Core.Scheduling;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

/// <summary>What happened to one file a delivery named. Path and detail are already safe to show.</summary>
public sealed record WebhookFileResult(string Path, ArrWebhookOutcome Outcome, string Detail)
{
    /// <summary>
    /// The one place caller text becomes a result: whatever the manager sent is cleaned and cut here, so
    /// the log, the receipt and the saved outcome all carry the same safe text.
    /// </summary>
    public static WebhookFileResult Of(string path, ArrWebhookOutcome outcome, string detail) => new(
        ArrWebhookText.Shown(path, ArrWebhookText.MaxPathLength),
        outcome,
        ArrWebhookText.Shown(detail, ArrWebhookText.MaxDetailLength));
}

public sealed record WebhookDeliveryResult(ArrWebhookOutcome Outcome, string Detail, IReadOnlyList<WebhookFileResult> Files);

/// <summary>
/// Acts on a Sonarr/Radarr webhook delivery: takes the files it names into the inventory, probes them,
/// and queues them only when the automatic optimise tick would. It never does more than that tick and a
/// library scan would have done together; it only does it sooner. A file still being written, or in a
/// library a scan is writing, is looked at again later. Every outcome is recorded on the connection so
/// the operator can see what happened to the last delivery.
/// </summary>
public sealed class ArrWebhookService(
    OptimisarrDbContext db,
    LibraryScanner scanner,
    LibraryInventoryService inventory,
    JobEnqueueService enqueue,
    ArrWebhookSettlingQueue settling,
    ArrWebhookIntakeOptions options,
    QueueDispatcher dispatcher,
    TimeProvider clock,
    ILogger<ArrWebhookService> logger)
{
    /// <summary>How many times a file is looked at again before it is left to the scan.</summary>
    public const int MaxSettlingAttempts = 10;

    private sealed record ReportedFile(string ArrPath, int Attempts);

    /// <summary>A file in the inventory, probed, in a library that would queue it now.</summary>
    private sealed record ReadyFile(Data.Library Library, MediaFile File);

    private sealed record ReadyAt(int Index, ReadyFile Ready);

    public async Task<WebhookDeliveryResult> HandleAsync(ArrConnection connection, ArrWebhookEvent delivery, CancellationToken cancellationToken)
    {
        var result = delivery switch
        {
            ArrWebhookEvent.Test => new WebhookDeliveryResult(ArrWebhookOutcome.Test, "Test received.", []),
            ArrWebhookEvent.Ignored ignored => new WebhookDeliveryResult(ArrWebhookOutcome.Ignored,
                $"{ArrWebhookText.Shown(ignored.EventType, ArrWebhookText.MaxEventTypeLength)} events are not used.", []),
            ArrWebhookEvent.Invalid invalid => new WebhookDeliveryResult(ArrWebhookOutcome.Refused, invalid.Reason, []),
            ArrWebhookEvent.Import when !connection.Enabled => new WebhookDeliveryResult(
                ArrWebhookOutcome.ConnectionOff, "This connection is turned off in Optimisarr.", []),
            ArrWebhookEvent.Import import => Summarise(await TakeInAsync(
                connection, [.. import.Paths.Select(path => new ReportedFile(path, Attempts: 0))], cancellationToken)),
            _ => throw new ArgumentOutOfRangeException(nameof(delivery)),
        };

        connection.WebhookLastReceivedAt = clock.GetUtcNow();
        return await RecordAsync(connection, result, "webhook", cancellationToken);
    }

    /// <summary>Looks again at a file that could not be taken in when its webhook arrived.</summary>
    public async Task RevisitAsync(SettlingWebhookFile file, CancellationToken cancellationToken)
    {
        var connection = await db.ArrConnections.FirstOrDefaultAsync(c => c.Id == file.ConnectionId, cancellationToken);
        if (connection is null || connection.WebhookSecretFingerprint is null || !connection.Enabled)
        {
            return;
        }

        var result = Summarise(await TakeInAsync(connection, [new ReportedFile(file.ArrPath, file.Attempts)], cancellationToken));
        await RecordAsync(connection, result, "webhook, looked at again", cancellationToken);
    }

    private async Task<WebhookDeliveryResult> RecordAsync(
        ArrConnection connection, WebhookDeliveryResult result, string stage, CancellationToken cancellationToken)
    {
        connection.WebhookLastOutcome = result.Outcome;
        connection.WebhookLastDetail = ArrWebhookText.Shown(result.Detail, ArrWebhookText.MaxDetailLength);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The operator removed the connection while its delivery was handled. Whatever was taken in
            // stays, as a scan would leave it; there is just nowhere left to record the outcome.
            logger.LogInformation("Arr connection {Id} was removed while its {Stage} was handled", connection.Id, stage);
            return new WebhookDeliveryResult(ArrWebhookOutcome.Ignored, "This connection was removed in Optimisarr.", result.Files);
        }

        logger.LogInformation("{Name} {Stage}: {Outcome}. {Detail}", connection.Name, stage, result.Outcome, result.Detail);
        return result;
    }

    private async Task<IReadOnlyList<WebhookFileResult>> TakeInAsync(
        ArrConnection connection, IReadOnlyList<ReportedFile> reported, CancellationToken cancellationToken)
    {
        var libraries = await db.Libraries.Where(library => library.Enabled).ToListAsync(cancellationToken);
        var roots = libraries.Select(library => new WebhookLibraryRoot(library.Id, library.Path)).ToList();
        // Once a scan has kept one library busy past the short wait, the rest of its files go straight
        // back on the list, so a season pack never waits that long once per episode.
        var scanning = new HashSet<int>();
        var results = new WebhookFileResult[reported.Count];
        var ready = new List<ReadyAt>();
        for (var index = 0; index < reported.Count; index++)
        {
            var (result, readyFile) = await TakeInOneAsync(connection, reported[index], libraries, roots, scanning, cancellationToken);
            if (readyFile is null)
            {
                results[index] = result!;
            }
            else
            {
                ready.Add(new ReadyAt(index, readyFile));
            }
        }

        await QueueReadyAsync(ready, results, cancellationToken);
        return results;
    }

    private async Task<(WebhookFileResult? Result, ReadyFile? Ready)> TakeInOneAsync(
        ArrConnection connection,
        ReportedFile reported,
        IReadOnlyList<Data.Library> libraries,
        IReadOnlyList<WebhookLibraryRoot> roots,
        HashSet<int> scanning,
        CancellationToken cancellationToken)
    {
        var resolution = ArrWebhookPathResolver.Resolve(reported.ArrPath, MappingOf(connection), roots);
        switch (resolution)
        {
            case WebhookPathResolution.Refused refused:
                return (WebhookFileResult.Of(reported.ArrPath, ArrWebhookOutcome.Refused, refused.Reason), null);
            case WebhookPathResolution.NotInLibrary notInLibrary:
                return (WebhookFileResult.Of(notInLibrary.LocalPath, ArrWebhookOutcome.NotInLibrary,
                    $"{notInLibrary.LocalPath} is not inside any enabled library."), null);
        }

        var found = (WebhookPathResolution.InLibrary)resolution;
        var library = libraries.First(candidate => candidate.Id == found.LibraryId);
        var scan = ScanIn(library, found.LocalPath);

        // A file already in the inventory stays with the library that found it, as a scan would leave it.
        // Looked up by the spelling on disk, the path its row was recorded under.
        if (scan is SingleFileScan.Settled first)
        {
            var existing = await db.MediaFiles.AsNoTracking().Where(file => file.Path == first.File.AbsolutePath)
                .Select(file => new { file.LibraryId }).FirstOrDefaultAsync(cancellationToken);
            if (existing is { LibraryId: null })
            {
                return (WebhookFileResult.Of(first.File.AbsolutePath, ArrWebhookOutcome.Refused,
                    "This file is in the inventory without a library. Scan its library to take it in."), null);
            }

            if (existing?.LibraryId is { } ownerId && ownerId != library.Id)
            {
                if (libraries.FirstOrDefault(candidate => candidate.Id == ownerId) is not { } owner)
                {
                    return (WebhookFileResult.Of(first.File.AbsolutePath, ArrWebhookOutcome.NotInLibrary,
                        $"{first.File.AbsolutePath} belongs to a library that is turned off."), null);
                }
                library = owner;
                scan = ScanIn(library, first.File.AbsolutePath);
            }
        }

        switch (scan)
        {
            case SingleFileScan.Missing:
                return (WebhookFileResult.Of(found.LocalPath, ArrWebhookOutcome.Refused,
                    $"Optimisarr cannot find {found.LocalPath}. If the manager sees the library under another folder, set a path mapping."), null);
            case SingleFileScan.NotMedia:
                return (WebhookFileResult.Of(found.LocalPath, ArrWebhookOutcome.NotQueued, $"{library.Name} does not hold this type of file."), null);
            case SingleFileScan.Refused refused:
                return (WebhookFileResult.Of(found.LocalPath, ArrWebhookOutcome.Refused, refused.Reason), null);
            case SingleFileScan.Settling pending:
                return (Defer(connection, reported, found.LocalPath, pending.SettlesAt + TimeSpan.FromSeconds(5),
                    ArrWebhookOutcome.Settling, $"{found.LocalPath} is still being written."), null);
            case SingleFileScan.Settled settled:
                return await RecordAndProbeAsync(connection, reported, library, settled.File, scanning, cancellationToken);
            default:
                throw new InvalidOperationException("Unknown scan result.");
        }
    }

    private SingleFileScan ScanIn(Data.Library library, string path) => scanner.ScanFile(
        library.Path, path, new LibraryScanOptions { Extensions = LibraryScanner.ExtensionsFor(library.MediaType) }, clock.GetUtcNow());

    private async Task<(WebhookFileResult? Result, ReadyFile? Ready)> RecordAndProbeAsync(
        ArrConnection connection,
        ReportedFile reported,
        Data.Library library,
        ScannedFile scanned,
        HashSet<int> scanning,
        CancellationToken cancellationToken)
    {
        var file = scanning.Contains(library.Id)
            ? null
            : await inventory.TryRecordScannedFileAsync(library, scanned, options.LibraryLockWait, cancellationToken);
        if (file is null)
        {
            scanning.Add(library.Id);
            return (Defer(connection, reported, scanned.AbsolutePath, clock.GetUtcNow() + options.ScanRetryDelay,
                ArrWebhookOutcome.WaitingForScan, $"A scan of {library.Name} is recording its files."), null);
        }

        if (file.Status == MediaFileStatus.Discovered)
        {
            file = await inventory.ProbeAsync(file.Id, cancellationToken) ?? file;
        }

        if (file.Status == MediaFileStatus.ProbeFailed)
        {
            return (WebhookFileResult.Of(file.Path, ArrWebhookOutcome.NotQueued, $"The file could not be probed: {file.ProbeError}"), null);
        }

        // The same decision AutoEnqueueWorker makes each minute, so nothing is queued that it would not queue.
        if (!library.AutoEnqueueEnabled)
        {
            return (WebhookFileResult.Of(file.Path, ArrWebhookOutcome.NotQueued,
                $"Added to the inventory. {library.Name} does not optimise automatically, so it was not queued."), null);
        }

        if (!AutoEnqueueScheduleEvaluator.ShouldEnqueueNow(
                library.AutoEnqueueEnabled, library.AutoEnqueueWindowStart, library.AutoEnqueueWindowEnd,
                TimeOnly.FromDateTime(clock.GetLocalNow().DateTime)))
        {
            return (WebhookFileResult.Of(file.Path, ArrWebhookOutcome.NotQueued,
                $"Added to the inventory. {library.Name} is outside its schedule; it is queued when the schedule opens if its rules select it."), null);
        }

        return (null, new ReadyFile(library, file));
    }

    private WebhookFileResult Defer(
        ArrConnection connection, ReportedFile reported, string localPath, DateTimeOffset due, ArrWebhookOutcome outcome, string why)
    {
        if (reported.Attempts >= MaxSettlingAttempts)
        {
            return WebhookFileResult.Of(localPath, ArrWebhookOutcome.NotQueued,
                $"{why} It was looked at {MaxSettlingAttempts} times, so it is left to the next library scan.");
        }

        return settling.Schedule(new SettlingWebhookFile(connection.Id, reported.ArrPath, localPath, due, reported.Attempts + 1))
            ? WebhookFileResult.Of(localPath, outcome, $"{why} It is looked at again after {due:u}.")
            : WebhookFileResult.Of(localPath, outcome, $"{why} The next library scan picks it up.");
    }

    private async Task QueueReadyAsync(IReadOnlyList<ReadyAt> ready, WebhookFileResult[] results, CancellationToken cancellationToken)
    {
        var queued = await enqueue.EnqueueEligibleFilesAsync(
            [.. ready.GroupBy(at => at.Ready.Library.Id)
                .Select(group => new ReportedFiles(group.First().Ready.Library, [.. group.Select(at => at.Ready.File.Id)]))],
            cancellationToken);

        var anyQueued = false;
        foreach (var (index, (library, file)) in ready)
        {
            var outcome = queued[file.Id];
            results[index] = outcome.Outcome switch
            {
                FileEnqueueOutcome.Enqueued => WebhookFileResult.Of(file.Path, ArrWebhookOutcome.Queued, $"Queued {file.RelativePath}."),
                FileEnqueueOutcome.AlreadyActive => WebhookFileResult.Of(file.Path, ArrWebhookOutcome.NotQueued, "The file is already queued."),
                FileEnqueueOutcome.Importing => WebhookFileResult.Of(file.Path, ArrWebhookOutcome.NotQueued,
                    $"{outcome.Reason} It is queued automatically once the import finishes."),
                _ => WebhookFileResult.Of(file.Path, ArrWebhookOutcome.NotQueued, outcome.Reason ?? "The library's rules do not select this file."),
            };
            if (outcome.Outcome == FileEnqueueOutcome.Enqueued)
            {
                library.LastAutoEnqueueAt = clock.GetUtcNow();
                anyQueued = true;
            }
        }

        if (anyQueued)
        {
            await db.SaveChangesAsync(cancellationToken);
            dispatcher.Wake();
        }
    }

    // One delivery can name a whole season. The summary leads with the file that moved furthest along.
    private static WebhookDeliveryResult Summarise(IReadOnlyList<WebhookFileResult> files)
    {
        var lead = files.OrderBy(file => Array.IndexOf(SummaryOrder, file.Outcome)).First();
        return files.Count == 1
            ? new(lead.Outcome, lead.Detail, files)
            : new(lead.Outcome, $"{lead.Detail} (and {files.Count - 1} more file(s))", files);
    }

    private static readonly ArrWebhookOutcome[] SummaryOrder =
    [
        ArrWebhookOutcome.Queued,
        ArrWebhookOutcome.Settling,
        ArrWebhookOutcome.WaitingForScan,
        ArrWebhookOutcome.NotQueued,
        ArrWebhookOutcome.Refused,
        ArrWebhookOutcome.NotInLibrary,
    ];

    private static ArrPathMapping? MappingOf(ArrConnection connection) =>
        string.IsNullOrWhiteSpace(connection.RemotePathPrefix) || string.IsNullOrWhiteSpace(connection.LocalPathPrefix)
            ? null
            : new ArrPathMapping(connection.RemotePathPrefix, connection.LocalPathPrefix);
}
