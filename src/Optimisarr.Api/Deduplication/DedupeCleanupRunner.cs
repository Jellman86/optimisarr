using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Core.Deduplication;
using Optimisarr.Core.Library;
using Optimisarr.Data;

namespace Optimisarr.Api.Deduplication;

/// <summary>What a cleanup would do now.</summary>
/// <param name="Ready">False when the report is missing or out of date, so nothing can start.</param>
/// <param name="Groups">Groups with a copy that can move.</param>
/// <param name="Copies">Copies that would move.</param>
/// <param name="BytesToFree">Their total size, freed once quarantine is purged.</param>
/// <param name="LeftAlone">Groups left alone, by reason.</param>
public sealed record DedupeCleanupPreview(bool Ready, int Groups, int Copies, long BytesToFree, IReadOnlyDictionary<string, int> LeftAlone);

/// <summary>One group a run could not clean.</summary>
public sealed record DedupeCleanupFailure(string Path, string Reason);

/// <summary>Where a cleanup run stands.</summary>
public sealed record DedupeCleanupStatus(int LibraryId, bool Running, int Total, int Done, int Moved, long BytesHeld, IReadOnlyList<DedupeCleanupFailure> Failures);

public enum DedupeCleanupStart { Started, AlreadyRunning, NotReady, NothingToDo }

/// <summary>
/// Moves the spare copies a report found into quarantine, one group at a time, in the background:
/// each move reads the files in full first, which takes a while on large films. Every move goes
/// through the journal and the last-moment preflight, so this decides nothing a person could not.
/// One run at a time. A restart stops it, and the journal settles whichever move was under way. The
/// plan is decided at the start, and the managers are asked again just before each group moves.
/// </summary>
public sealed class DedupeCleanupRunner(
    IServiceScopeFactory scopes,
    ExactDuplicateStore store,
    DedupeJournalStore journal,
    DedupeQuarantineService quarantine,
    IHostApplicationLifetime lifetime,
    ILogger<DedupeCleanupRunner> logger,
    TimeSpan? changeSettle = null)
{
    private readonly Lock _lock = new();
    private DedupeCleanupStatus? _status;
    private CancellationTokenSource? _cancel;
    private Task _run = Task.CompletedTask;

    /// <summary>Lets a test wait for the run it started.</summary>
    internal Task Completion => _run;

    public DedupeCleanupStatus Status(int libraryId)
    {
        lock (_lock)
        {
            return _status is { } status && status.LibraryId == libraryId
                ? status
                : new DedupeCleanupStatus(libraryId, false, 0, 0, 0, 0, []);
        }
    }

    public async Task<DedupeCleanupPreview> PreviewAsync(int libraryId, CancellationToken token)
    {
        var inputs = await store.ReadCleanupGroupsAsync(libraryId, token);
        if (inputs is null) return new DedupeCleanupPreview(false, 0, 0, 0, new Dictionary<string, int>());
        var decisions = Decide(inputs, await TrackingAsync(token));
        var spareSizes = decisions.Where(item => item.Decision.Refusal is null)
            .Select(item => (item.Group.SizeBytes, item.Decision.SpareIds.Count)).ToList();
        return new DedupeCleanupPreview(
            true,
            spareSizes.Count,
            spareSizes.Sum(item => item.Count),
            spareSizes.Sum(item => item.SizeBytes * item.Count),
            decisions.Where(item => item.Decision.Refusal is not null)
                .GroupBy(item => item.Decision.Refusal!.Value.ToString())
                .ToDictionary(group => group.Key, group => group.Count()));
    }

    public async Task<DedupeCleanupStart> TryStartAsync(int libraryId, CancellationToken token)
    {
        lock (_lock)
        {
            if (_status is { Running: true }) return DedupeCleanupStart.AlreadyRunning;
        }
        var inputs = await store.ReadCleanupGroupsAsync(libraryId, token);
        if (inputs is null) return DedupeCleanupStart.NotReady;
        var work = Decide(inputs, await TrackingAsync(token)).Where(item => item.Decision.Refusal is null).ToList();
        if (work.Count == 0) return DedupeCleanupStart.NothingToDo;

        lock (_lock)
        {
            if (_status is { Running: true }) return DedupeCleanupStart.AlreadyRunning;
            _cancel?.Dispose();
            _cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            _status = new DedupeCleanupStatus(libraryId, true, work.Count, 0, 0, 0, []);
            var cancellation = _cancel.Token;
            _run = Task.Run(() => RunAsync(libraryId, inputs, work, cancellation), CancellationToken.None);
        }
        return DedupeCleanupStart.Started;
    }

    public bool Cancel(int libraryId)
    {
        lock (_lock)
        {
            if (_status is not { Running: true } status || status.LibraryId != libraryId) return false;
            _cancel?.Cancel();
            return true;
        }
    }

    private async Task RunAsync(int libraryId, DedupeCleanupGroups inputs,
        IReadOnlyList<(ExactDuplicateGroup Group, DedupeCleanupDecision Decision)> work, CancellationToken token)
    {
        var failures = new List<DedupeCleanupFailure>();
        var done = 0;
        var moved = 0;
        var bytes = 0L;
        try
        {
            foreach (var (group, decision) in work)
            {
                token.ThrowIfCancellationRequested();
                var failure = await CleanGroupAsync(libraryId, inputs, group, decision, token);
                if (failure is null)
                {
                    moved += decision.SpareIds.Count;
                    bytes += group.SizeBytes * decision.SpareIds.Count;
                }
                else
                {
                    failures.Add(failure);
                }
                done++;
                Publish(libraryId, true, work.Count, done, moved, bytes, failures);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            logger.LogInformation("Duplicate cleanup of library {LibraryId} stopped after {Done} groups", libraryId, done);
        }
        catch (Exception error)
        {
            logger.LogError(error, "Duplicate cleanup of library {LibraryId} failed", libraryId);
            failures.Add(new DedupeCleanupFailure(string.Empty, error.Message));
        }
        Publish(libraryId, false, work.Count, done, moved, bytes, failures);
    }

    private async Task<DedupeCleanupFailure?> CleanGroupAsync(int libraryId, DedupeCleanupGroups inputs, ExactDuplicateGroup group,
        DedupeCleanupDecision decision, CancellationToken token)
    {
        var first = group.Copies[0].AbsolutePath ?? group.Copies[0].RelativePath;
        var plan = BuildPlan(libraryId, inputs, group, decision);
        if (plan is null) return new DedupeCleanupFailure(first, "This group changed since the scan. Scan again.");
        if (await journal.TryPrepareAsync(plan, token) != DedupePreparationResult.Created)
            return new DedupeCleanupFailure(first, "Another operation is using these files, or they changed since the scan.");

        DedupeActionResult result;
        try
        {
            result = await quarantine.QuarantineAsync(plan.Id, token, changeSettle);
        }
        finally
        {
            // However this ended, an action that never started moving must not keep holding its files.
            // Only a still-prepared action can be cancelled, so this cannot disturb a move.
            await journal.CancelPreparedAsync(plan.Id, CancellationToken.None);
        }
        if (result.Outcome == DedupeActionOutcome.Completed) return null;
        var issues = result.Issues.Select(issue => Describe(issue.Refusal)).Distinct();
        return new DedupeCleanupFailure(first, string.Join(" ", new[] { result.Reason }.Concat(issues).OfType<string>()));
    }

    private static string Describe(DedupePreflightRefusal refusal) => refusal switch
    {
        DedupePreflightRefusal.ManagerUnavailable => "Sonarr or Radarr could not be asked. Check the connection and try again.",
        DedupePreflightRefusal.TrackedByManager => "Sonarr or Radarr now tracks this copy. Scan again.",
        DedupePreflightRefusal.PossiblyTrackedByManager => "Sonarr or Radarr may track this copy, so choose a keeper to clean it.",
        DedupePreflightRefusal.StorageUnqualified => "Cleanup is not available on this storage.",
        DedupePreflightRefusal.Missing => "A file is gone. Scan again.",
        DedupePreflightRefusal.Link => "A file is a link, which cleanup does not move.",
        DedupePreflightRefusal.HardLinked => "A copy has other hard links, so moving it would free nothing.",
        DedupePreflightRefusal.NotIndependent => "A file is part of a bundle that has to stay together.",
        DedupePreflightRefusal.Changed or DedupePreflightRefusal.ContentDiffers => "A file changed since the scan. Scan again.",
        DedupePreflightRefusal.NotRead => "A file could not be read in full. Try again when it is not in use.",
        DedupePreflightRefusal.SamePhysicalFile => "Two of these paths are the same file. Scan again.",
        DedupePreflightRefusal.InUse => "A job is using a file. Try again when it finishes.",
        DedupePreflightRefusal.NotWritable => "A folder cannot be written to.",
        DedupePreflightRefusal.DestinationOccupied => "Something already occupies the quarantine path.",
        DedupePreflightRefusal.UnsafeDestination => "A folder on the way to quarantine is redirected.",
        DedupePreflightRefusal.CrossFilesystem => "Quarantine is on another filesystem, so the copy cannot be renamed there.",
        _ => "The plan is no longer valid. Scan again.",
    };

    private static DedupePreparation? BuildPlan(int libraryId, DedupeCleanupGroups inputs, ExactDuplicateGroup group, DedupeCleanupDecision decision)
    {
        var id = Guid.NewGuid();
        var files = new List<DedupeFileSnapshot>();
        foreach (var copy in group.Copies)
        {
            if (!inputs.Rows.TryGetValue(copy.Id, out var row) || row.Identity is null) return null;
            var keeper = decision.KeepIds.Contains(copy.Id);
            files.Add(new DedupeFileSnapshot(copy.Id, row.LibraryId, row.Root, row.Path,
                keeper ? null : DedupeQuarantinePaths.For(row.Root, id, row.Path),
                row.Identity, row.SizeBytes, row.ModifiedUtcTicks, row.Sha256, keeper));
        }
        return new DedupePreparation(id, libraryId, group.ReviewKey ?? string.Empty, inputs.ScopeKey, files);
    }

    private static List<(ExactDuplicateGroup Group, DedupeCleanupDecision Decision)> Decide(DedupeCleanupGroups inputs, ArrTracking tracking) =>
        inputs.Groups.Select(group =>
        {
            var copies = group.Copies.Select(copy => new DedupeCopy(copy.Id, copy.LibraryId, copy.RelativePath, copy.LibraryType, copy.HardLinkCount,
                tracking.TrackedBy.ContainsKey(copy.Id) || (copy.Aliases?.Any(alias => tracking.TrackedBy.ContainsKey(alias.Id)) ?? false),
                tracking.PossiblyTrackedBy.ContainsKey(copy.Id) || (copy.Aliases?.Any(alias => tracking.PossiblyTrackedBy.ContainsKey(alias.Id)) ?? false)))
                .ToList();
            return (group, DedupeKeeperRule.Decide(copies, tracking.Complete, group.Decision?.Choice, group.Decision?.KeepIds));
        }).ToList();

    private async Task<ArrTracking> TrackingAsync(CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ArrTrackedFilesService>().GetAsync(token);
    }

    private void Publish(int libraryId, bool running, int total, int done, int moved, long bytes, List<DedupeCleanupFailure> failures)
    {
        lock (_lock)
        {
            _status = new DedupeCleanupStatus(libraryId, running, total, done, moved, bytes, [.. failures]);
        }
    }
}
