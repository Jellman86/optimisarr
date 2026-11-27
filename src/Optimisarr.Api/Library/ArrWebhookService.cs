using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Activity;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Core.Scheduling;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

public sealed record WebhookFileResult(string Path, ArrWebhookOutcome Outcome, string Detail);

public sealed record WebhookDeliveryResult(ArrWebhookOutcome Outcome, string Detail, IReadOnlyList<WebhookFileResult> Files);

/// <summary>
/// Acts on a Sonarr/Radarr webhook delivery: takes the files it names into the inventory, probes them,
/// and queues them only when the automatic optimise tick would. It never does more than that tick and a
/// library scan would have done together; it only does it sooner. A file still being written is looked at
/// again once it settles. Every outcome is recorded on the connection so the operator can see what
/// happened to the last delivery.
/// </summary>
public sealed class ArrWebhookService(
    OptimisarrDbContext db,
    LibraryScanner scanner,
    LibraryInventoryService inventory,
    JobEnqueueService enqueue,
    ArrWebhookSettlingQueue settling,
    QueueDispatcher dispatcher,
    TimeProvider clock,
    ILogger<ArrWebhookService> logger)
{
    /// <summary>How many times a file that keeps changing is looked at again before it is left to the scan.</summary>
    public const int MaxSettlingAttempts = 10;

    public async Task<WebhookDeliveryResult> HandleAsync(ArrConnection connection, ArrWebhookEvent delivery, CancellationToken cancellationToken)
    {
        var result = delivery switch
        {
            ArrWebhookEvent.Test => new WebhookDeliveryResult(ArrWebhookOutcome.Test, "Test received.", []),
            ArrWebhookEvent.Ignored ignored => new WebhookDeliveryResult(
                ArrWebhookOutcome.Ignored, $"{ignored.EventType} events are not used.", []),
            ArrWebhookEvent.Invalid invalid => new WebhookDeliveryResult(ArrWebhookOutcome.Refused, invalid.Reason, []),
            ArrWebhookEvent.Import when !connection.Enabled => new WebhookDeliveryResult(
                ArrWebhookOutcome.ConnectionOff, "This connection is turned off in Optimisarr.", []),
            ArrWebhookEvent.Import import => Summarise(await TakeInAllAsync(connection, import, cancellationToken)),
            _ => throw new ArgumentOutOfRangeException(nameof(delivery)),
        };

        connection.WebhookLastReceivedAt = clock.GetUtcNow();
        Record(connection, result.Outcome, result.Detail);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("{Name} webhook: {Outcome}. {Detail}", connection.Name, result.Outcome, result.Detail);
        return result;
    }

    /// <summary>Looks again at a file that was still settling when its webhook arrived.</summary>
    public async Task RevisitAsync(SettlingWebhookFile file, CancellationToken cancellationToken)
    {
        var connection = await db.ArrConnections.FirstOrDefaultAsync(c => c.Id == file.ConnectionId, cancellationToken);
        if (connection is null || connection.WebhookSecretFingerprint is null || !connection.Enabled)
        {
            return;
        }

        var result = await TakeInAsync(connection, file.ArrPath, file.Attempts, cancellationToken);
        Record(connection, result.Outcome, result.Detail);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("{Name} webhook, after settling: {Outcome}. {Detail}", connection.Name, result.Outcome, result.Detail);
    }

    private async Task<IReadOnlyList<WebhookFileResult>> TakeInAllAsync(
        ArrConnection connection, ArrWebhookEvent.Import import, CancellationToken cancellationToken)
    {
        var results = new List<WebhookFileResult>(import.Paths.Count);
        foreach (var path in import.Paths)
        {
            results.Add(await TakeInAsync(connection, path, attempts: 0, cancellationToken));
        }
        return results;
    }

    private async Task<WebhookFileResult> TakeInAsync(
        ArrConnection connection, string arrPath, int attempts, CancellationToken cancellationToken)
    {
        var libraries = await db.Libraries.Where(library => library.Enabled).ToListAsync(cancellationToken);
        var resolution = ArrWebhookPathResolver.Resolve(
            arrPath,
            MappingOf(connection),
            [.. libraries.Select(library => new WebhookLibraryRoot(library.Id, library.Path))]);

        switch (resolution)
        {
            case WebhookPathResolution.Refused refused:
                return new(arrPath, ArrWebhookOutcome.Refused, refused.Reason);
            case WebhookPathResolution.NotInLibrary notInLibrary:
                return new(notInLibrary.LocalPath, ArrWebhookOutcome.NotInLibrary,
                    $"{notInLibrary.LocalPath} is not inside any enabled library.");
        }

        var found = (WebhookPathResolution.InLibrary)resolution;
        // A file already in the inventory stays with the library that found it, as a scan would leave it.
        var existing = await db.MediaFiles.AsNoTracking().Where(file => file.Path == found.LocalPath)
            .Select(file => new { file.LibraryId }).FirstOrDefaultAsync(cancellationToken);
        if (existing is { LibraryId: null })
        {
            return new(found.LocalPath, ArrWebhookOutcome.Refused,
                "This file is in the inventory without a library. Scan its library to take it in.");
        }

        var ownerId = existing?.LibraryId ?? found.LibraryId;
        var library = libraries.FirstOrDefault(candidate => candidate.Id == ownerId);
        if (library is null)
        {
            return new(found.LocalPath, ArrWebhookOutcome.NotInLibrary,
                $"{found.LocalPath} belongs to a library that is turned off.");
        }

        var options = new LibraryScanOptions { Extensions = LibraryScanner.ExtensionsFor(library.MediaType) };
        switch (scanner.ScanFile(library.Path, found.LocalPath, options, clock.GetUtcNow()))
        {
            case SingleFileScan.Missing:
                return new(found.LocalPath, ArrWebhookOutcome.Refused,
                    $"Optimisarr cannot find {found.LocalPath}. If the manager sees the library under another folder, set a path mapping.");
            case SingleFileScan.NotMedia:
                return new(found.LocalPath, ArrWebhookOutcome.NotQueued, $"{library.Name} does not hold this type of file.");
            case SingleFileScan.Refused refused:
                return new(found.LocalPath, ArrWebhookOutcome.Refused, refused.Reason);
            case SingleFileScan.Settling pending:
                return Defer(connection, arrPath, found.LocalPath, pending.SettlesAt, attempts);
            case SingleFileScan.Settled settled:
                return await QueueIfAutomaticAsync(library, settled.File, cancellationToken);
            default:
                throw new InvalidOperationException("Unknown scan result.");
        }
    }

    private WebhookFileResult Defer(ArrConnection connection, string arrPath, string localPath, DateTimeOffset settlesAt, int attempts)
    {
        if (attempts >= MaxSettlingAttempts)
        {
            return new(localPath, ArrWebhookOutcome.NotQueued,
                "The file kept changing, so it is left to the next library scan.");
        }

        // A moment past the settling period, so the next look finds it settled rather than borderline.
        var due = settlesAt + TimeSpan.FromSeconds(5);
        return settling.Schedule(new SettlingWebhookFile(connection.Id, arrPath, due, attempts + 1))
            ? new(localPath, ArrWebhookOutcome.Settling, $"{localPath} is still being written. It is looked at again after {due:u}.")
            : new(localPath, ArrWebhookOutcome.Settling, $"{localPath} is still being written. The next library scan picks it up.");
    }

    private async Task<WebhookFileResult> QueueIfAutomaticAsync(Data.Library library, ScannedFile scanned, CancellationToken cancellationToken)
    {
        var file = await inventory.RecordScannedFileAsync(library, scanned, cancellationToken);
        if (file.Status == MediaFileStatus.Discovered)
        {
            file = await inventory.ProbeAsync(file.Id, cancellationToken) ?? file;
        }

        if (file.Status == MediaFileStatus.ProbeFailed)
        {
            return new(file.Path, ArrWebhookOutcome.NotQueued, $"The file could not be probed: {file.ProbeError}");
        }

        // The same decision AutoEnqueueWorker makes each minute, so nothing is queued that it would not queue.
        if (!library.AutoEnqueueEnabled)
        {
            return new(file.Path, ArrWebhookOutcome.NotQueued,
                $"Added to the inventory. {library.Name} does not optimise automatically, so it was not queued.");
        }

        if (!AutoEnqueueScheduleEvaluator.ShouldEnqueueNow(
                library.AutoEnqueueEnabled, library.AutoEnqueueWindowStart, library.AutoEnqueueWindowEnd,
                TimeOnly.FromDateTime(clock.GetLocalNow().DateTime)))
        {
            return new(file.Path, ArrWebhookOutcome.NotQueued,
                $"Added to the inventory. {library.Name} is outside its schedule; it is queued when the schedule opens if its rules select it.");
        }

        var queued = await enqueue.EnqueueEligibleFileAsync(library, file.Id, cancellationToken);
        switch (queued.Outcome)
        {
            case FileEnqueueOutcome.Enqueued:
                library.LastAutoEnqueueAt = clock.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken);
                dispatcher.Wake();
                return new(file.Path, ArrWebhookOutcome.Queued, $"Queued {file.RelativePath}.");
            case FileEnqueueOutcome.AlreadyActive:
                return new(file.Path, ArrWebhookOutcome.NotQueued, "The file is already queued.");
            case FileEnqueueOutcome.Importing:
                return new(file.Path, ArrWebhookOutcome.NotQueued,
                    $"{queued.Reason} It is queued automatically once the import finishes.");
            default:
                return new(file.Path, ArrWebhookOutcome.NotQueued, queued.Reason ?? "The library's rules do not select this file.");
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
        ArrWebhookOutcome.NotQueued,
        ArrWebhookOutcome.Refused,
        ArrWebhookOutcome.NotInLibrary,
    ];

    private static void Record(ArrConnection connection, ArrWebhookOutcome outcome, string detail)
    {
        connection.WebhookLastOutcome = outcome;
        connection.WebhookLastDetail = detail.Length > 1024 ? detail[..1024] : detail;
    }

    private static ArrPathMapping? MappingOf(ArrConnection connection) =>
        string.IsNullOrWhiteSpace(connection.RemotePathPrefix) || string.IsNullOrWhiteSpace(connection.LocalPathPrefix)
            ? null
            : new ArrPathMapping(connection.RemotePathPrefix, connection.LocalPathPrefix);
}
