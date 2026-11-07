using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Api.Replacement;
using Optimisarr.Core.Deduplication;
using Optimisarr.Data;

namespace Optimisarr.Api.Deduplication;

public enum DedupeActionOutcome { Completed, Refused, NeedsAttention, NotFound }

public sealed record DedupeActionResult(DedupeActionOutcome Outcome, IReadOnlyList<DedupePreflightIssue> Issues, string? Reason)
{
    public static DedupeActionResult Completed { get; } = new(DedupeActionOutcome.Completed, [], null);
    public static DedupeActionResult NotFound { get; } = new(DedupeActionOutcome.NotFound, [], null);
    public static DedupeActionResult Refused(string reason, params DedupePreflightIssue[] issues) => new(DedupeActionOutcome.Refused, issues, reason);
    public static DedupeActionResult Attention(string reason) => new(DedupeActionOutcome.NeedsAttention, [], reason);
}

/// <summary>
/// Moves the copies of a prepared exact-duplicate plan into quarantine and back. The move is a
/// same-filesystem rename that can neither copy nor overwrite, and nothing is ever deleted: a
/// quarantined copy stays restorable until the operator separately decides otherwise.
/// <para>
/// The journal records intent before any file moves. Which files actually moved is read back from
/// the files themselves, not assumed from the journal, so an interrupted action is resolved by
/// evidence: a file that cannot be placed with certainty stops the action and keeps every
/// reservation for the operator rather than guessing.
/// </para>
/// </summary>
public sealed class DedupeQuarantineService(IServiceScopeFactory scopes, FileOperationClaims claims, ILogger<DedupeQuarantineService> logger)
{
    /// <param name="changeSettle">How long files must have been unchanged to be read; tests shorten it.</param>
    /// <param name="afterMove">Runs after each move, so tests can stop the action at that boundary.</param>
    /// <param name="beforeIntent">Runs just before intent is recorded, so tests can race cancellation.</param>
    public async Task<DedupeActionResult> QuarantineAsync(Guid id, CancellationToken token,
        TimeSpan? changeSettle = null, Action<DedupeFileSnapshot>? afterMove = null, Func<Task>? beforeIntent = null)
    {
        var loaded = await LoadAsync(id, token);
        if (loaded is not { } action) return DedupeActionResult.NotFound;
        if (action.State == DedupeActionState.Quarantined) return DedupeActionResult.Completed;
        if (action.State != DedupeActionState.Prepared)
            return DedupeActionResult.Refused($"This action is {action.State} and cannot start a move.");
        // Only this layout is skipped by library scans; anywhere else the copy would return as media.
        if (action.Plan.Files.FirstOrDefault(f => !f.IsKeeper && f.QuarantinePath != DedupeQuarantinePaths.For(f.Root, id, f.Path)) is { } misplaced)
            return DedupeActionResult.Refused($"{misplaced.Path} has a quarantine destination outside the action's folder.",
                new DedupePreflightIssue(misplaced.MediaFileId, DedupePreflightRefusal.UnsafeDestination));

        using var held = claims.TryAcquire(DedupeJournalStore.Keys(action.Plan.Files));
        if (held is null) return DedupeActionResult.Refused("Another file operation is using these files. Try again shortly.");

        var observations = new List<DedupeFileObservation>();
        foreach (var file in action.Plan.Files)
            observations.Add(await DedupeFileObserver.ObserveAsync(file, token, changeSettle));
        var issues = DedupeActionPreflight.Evaluate(action.Plan, observations, await ContextAsync(action.Plan, token)).Issues.ToList();
        var copies = action.Plan.Files.Where(f => !f.IsKeeper).ToArray();
        // Equal volume identifiers cannot prove a rename will work across bind mounts, so try one.
        if (issues.Count == 0)
            issues.AddRange(copies.Where(f => !CanRename(f)).Select(f => new DedupePreflightIssue(f.MediaFileId, DedupePreflightRefusal.CrossFilesystem)));
        if (issues.Count > 0)
        {
            RemoveEmptyQuarantineFolders(copies);
            return DedupeActionResult.Refused("The files cannot be moved safely.", issues.ToArray());
        }

        var stamps = observations.ToDictionary(o => o.MediaFileId, o => o.ChangeStamp);
        var keepers = action.Plan.Files.Where(f => f.IsKeeper).ToArray();
        if (beforeIntent is not null) await beforeIntent();
        if (!await TryTransitionAsync(id, DedupeActionState.Prepared, DedupeActionState.Quarantining, null))
        {
            RemoveEmptyQuarantineFolders(copies);
            return DedupeActionResult.Refused("This action changed before it could start, so nothing moved.");
        }

        // From here the journal always gets an answer, so cancelling undoes the moves rather than
        // abandoning them. Anything else escaping is resolved at restart, or by a later restore.
        try
        {
            foreach (var copy in copies)
            {
                if (token.IsCancellationRequested) return await UndoAsync(id, action, "The action was cancelled.");
                if (keepers.Append(copy).FirstOrDefault(f => !IsUnchangedSinceRead(f, stamps[f.MediaFileId])) is { } changed)
                    return await UndoAsync(id, action, $"{changed.Path} changed before the move.");
                if (!PathsAreSafe(copy, copy.QuarantinePath!) || !PathsAreSafe(copy, copy.Path))
                    return await UndoAsync(id, action, $"A folder on the way to {copy.QuarantinePath} is redirected.");
                Directory.CreateDirectory(Path.GetDirectoryName(copy.QuarantinePath!)!);
                var renamed = RenameOnly.Rename(copy.Path, copy.QuarantinePath!);
                if (renamed != RenameOutcome.Renamed)
                    return await UndoAsync(id, action, $"Could not move {copy.Path}: {renamed}.");
                afterMove?.Invoke(copy);
            }
            // The last move has to leave a reviewed keeper behind, or it must not stand.
            if (keepers.FirstOrDefault(f => !IsUnchangedSinceRead(f, stamps[f.MediaFileId])) is { } lost)
                return await UndoAsync(id, action, $"{lost.Path} changed during the move.");
            if (token.IsCancellationRequested) return await UndoAsync(id, action, "The action was cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Quarantine of dedupe action {ActionId} failed", id);
            return await UndoAsync(id, action, ex.Message);
        }

        await TransitionAsync(id, DedupeActionState.Quarantined, null);
        return DedupeActionResult.Completed;
    }

    /// <summary>
    /// Returns every copy to its exact original path and releases the reservations. A copy already
    /// back is fine; anything else that does not match the reviewed file, or an occupied original
    /// path, stops here and keeps the reservations, because no file is ever overwritten.
    /// </summary>
    public async Task<DedupeActionResult> RestoreAsync(Guid id, CancellationToken token)
    {
        var loaded = await LoadAsync(id, token);
        if (loaded is not { } action) return DedupeActionResult.NotFound;
        if (action.State == DedupeActionState.Restored) return DedupeActionResult.Completed;
        if (action.State is DedupeActionState.Prepared or DedupeActionState.Cancelled or DedupeActionState.Purging or DedupeActionState.Purged)
            return DedupeActionResult.Refused($"This action is {action.State} and has nothing to restore.");

        using var held = claims.TryAcquire(DedupeJournalStore.Keys(action.Plan.Files));
        if (held is null) return DedupeActionResult.Refused("Another file operation is using these files. Try again shortly.");
        // Holding the claim means no action is running, so a state left mid-way has nobody to finish
        // it: an earlier attempt stopped, and the files themselves say how far it got.
        if (action.State is DedupeActionState.Quarantining or DedupeActionState.Restoring)
            await TransitionAsync(id, DedupeActionState.NeedsAttention, "An earlier attempt stopped part-way.");
        await TransitionAsync(id, DedupeActionState.Restoring, null);
        return await ReturnCopiesAsync(id, action);
    }

    /// <summary>
    /// Deletes the quarantined copies for good, which is the only way this frees space. Nothing is
    /// deleted unless every keeper is read in full now and is still the reviewed file: a keeper that
    /// has changed may leave a quarantined copy as the last of those bytes, so it stays restorable.
    /// </summary>
    /// <param name="changeSettle">How long files must have been unchanged to be read; tests shorten it.</param>
    public async Task<DedupeActionResult> PurgeAsync(Guid id, CancellationToken token, TimeSpan? changeSettle = null)
    {
        var loaded = await LoadAsync(id, token);
        if (loaded is not { } action) return DedupeActionResult.NotFound;
        if (action.State == DedupeActionState.Purged) return DedupeActionResult.Completed;
        if (action.State is not (DedupeActionState.Quarantined or DedupeActionState.Purging))
            return DedupeActionResult.Refused($"This action is {action.State}, so its copies cannot be deleted.");

        using var held = claims.TryAcquire(DedupeJournalStore.Keys(action.Plan.Files));
        if (held is null) return DedupeActionResult.Refused("Another file operation is using these files. Try again shortly.");

        foreach (var keeper in action.Plan.Files.Where(f => f.IsKeeper))
        {
            var now = await DedupeFileObserver.ObserveAsync(keeper, token, changeSettle);
            if (!now.Exists || now.IsLink || now.Identity != keeper.Identity || now.SizeBytes != keeper.SizeBytes
                || now.ModifiedUtcTicks != keeper.ModifiedUtcTicks || !string.Equals(now.FreshSha256, keeper.Sha256, StringComparison.Ordinal))
                return DedupeActionResult.Refused(
                    $"{keeper.Path} is no longer the copy that was kept, so the quarantined copies stay and can be restored.");
        }

        var copies = action.Plan.Files.Where(f => !f.IsKeeper).ToArray();
        if (action.State == DedupeActionState.Quarantined
            && !await TryTransitionAsync(id, DedupeActionState.Quarantined, DedupeActionState.Purging, null))
            return DedupeActionResult.Refused("This action changed before it could start, so nothing was deleted.");

        try
        {
            foreach (var copy in copies)
            {
                if (!PathsAreSafe(copy, copy.QuarantinePath!))
                    return DedupeActionResult.Attention($"A folder on the way to {copy.QuarantinePath} is redirected, so it was left in place.");
                if (File.Exists(copy.QuarantinePath!)) File.Delete(copy.QuarantinePath!);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Purge of dedupe action {ActionId} stopped part-way", id);
            return DedupeActionResult.Attention(ex.Message);
        }

        RemoveEmptyQuarantineFolders(copies);
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
            var row = await db.DedupeActions.SingleAsync(a => a.Id == id, CancellationToken.None);
            row.State = DedupeActionState.Purged;
            row.AttentionReason = null;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.DedupeFileClaims.Where(c => c.ActionId == id).ExecuteDeleteAsync(CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }
        return DedupeActionResult.Completed;
    }

    private async Task<DedupeActionResult> UndoAsync(Guid id, LoadedAction action, string reason)
    {
        await TransitionAsync(id, DedupeActionState.NeedsAttention, reason);
        await TransitionAsync(id, DedupeActionState.Restoring, null);
        var undone = await ReturnCopiesAsync(id, action);
        return undone.Outcome == DedupeActionOutcome.Completed
            ? DedupeActionResult.Refused(reason + " Every file was returned to its original path.")
            : DedupeActionResult.Attention(reason + " " + undone.Reason);
    }

    // Not cancellable: once files are moving, stopping half way would only leave more to resolve.
    private async Task<DedupeActionResult> ReturnCopiesAsync(Guid id, LoadedAction action)
    {
        var token = CancellationToken.None;
        var copies = action.Plan.Files.Where(f => !f.IsKeeper).ToArray();
        string? problem = null;
        foreach (var copy in copies)
        {
            try { problem = await ReturnCopyAsync(copy); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problem = $"Could not restore {copy.Path}: {ex.Message}"; }
            if (problem is not null) break;
        }
        if (problem is not null)
        {
            await TransitionAsync(id, DedupeActionState.NeedsAttention, problem);
            return DedupeActionResult.Attention(problem);
        }

        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var row = await db.DedupeActions.SingleAsync(a => a.Id == id, token);
            row.State = DedupeActionState.Restored;
            row.AttentionReason = null;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.DedupeFileClaims.Where(c => c.ActionId == id).ExecuteDeleteAsync(token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        RemoveEmptyQuarantineFolders(copies);
        return DedupeActionResult.Completed;
    }

    // Null when the copy is back at its original path, otherwise why it could not be put there.
    private static async Task<string?> ReturnCopyAsync(DedupeFileSnapshot copy)
    {
        var quarantine = copy.QuarantinePath!;
        if (!PathsAreSafe(copy, copy.Path) || !PathsAreSafe(copy, quarantine))
            return $"A folder on the way to {copy.Path} is redirected, so {quarantine} was left in place.";
        var atQuarantine = Occupied(quarantine);
        var atOriginal = Occupied(copy.Path);
        if (!atQuarantine && atOriginal)
            return await HoldsReviewedContentAsync(copy, copy.Path) ? null : $"{copy.Path} is not the reviewed file.";
        if (!atQuarantine) return $"{copy.Path} is at neither its original path nor {quarantine}.";
        if (atOriginal) return $"{copy.Path} is occupied, so {quarantine} was left in place.";
        if (!await HoldsReviewedContentAsync(copy, quarantine)) return $"{quarantine} is not the reviewed file.";
        Directory.CreateDirectory(Path.GetDirectoryName(copy.Path)!);
        return RenameOnly.Rename(quarantine, copy.Path) switch
        {
            RenameOutcome.Renamed => null,
            var outcome => $"Could not restore {copy.Path}: {outcome}.",
        };
    }

    // A rename keeps the inode, size and modification time, so any difference means a different
    // file. The change time catches a rewrite that put the others back; a rename also moves it, so
    // it can only be compared for a file that has not moved since it was read.
    private static bool IsReviewedFile(DedupeFileSnapshot file, string path, string? changeStamp = null)
    {
        var info = new FileInfo(path);
        return info.Exists && info.LinkTarget is null && info.Length == file.SizeBytes && info.LastWriteTimeUtc.Ticks == file.ModifiedUtcTicks
            && ExactFileIdentity.Read(path) == file.Identity
            && (changeStamp is null || ExactFileIdentity.ChangeStamp(path)?.ToString() == changeStamp);
    }

    // Still at a path inside the library that the read vouched for. No stamp means no vouching.
    private static bool IsUnchangedSinceRead(DedupeFileSnapshot file, string? changeStamp) =>
        changeStamp is not null && PathsAreSafe(file, file.Path) && IsReviewedFile(file, file.Path, changeStamp);

    // The move's own rename changes the change time, so restoring proves the content by reading it.
    private static async Task<bool> HoldsReviewedContentAsync(DedupeFileSnapshot file, string path)
    {
        if (!IsReviewedFile(file, path)) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream)) == file.Sha256;
    }

    private static bool PathsAreSafe(DedupeFileSnapshot file, string path) => DedupeFileObserver.WalkFolders(file.Root, path).Safe;

    private static bool CanRename(DedupeFileSnapshot copy)
    {
        try { return FileMover.CanMoveAtomically(Path.GetDirectoryName(copy.Path)!, Path.GetDirectoryName(copy.QuarantinePath!)!); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool Occupied(string path) => Path.Exists(path) || new FileInfo(path).LinkTarget is not null;

    // Only empty folders on this action's paths, and only inside its quarantine folder.
    private static void RemoveEmptyQuarantineFolders(IEnumerable<DedupeFileSnapshot> copies)
    {
        foreach (var copy in copies)
        {
            for (var folder = Path.GetDirectoryName(copy.QuarantinePath!); folder is not null
                && DedupeQuarantinePaths.IsInside(copy.Root, folder); folder = Path.GetDirectoryName(folder))
            {
                try
                {
                    if (!Directory.Exists(folder) || Directory.EnumerateFileSystemEntries(folder).Any()) break;
                    Directory.Delete(folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { break; }
            }
        }
    }

    private sealed record LoadedAction(DedupeActionState State, DedupePreparation Plan);

    private async Task<LoadedAction?> LoadAsync(Guid id, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var action = await db.DedupeActions.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, token);
        if (action is null) return null;
        var files = await db.DedupeActionFiles.AsNoTracking().Where(f => f.ActionId == id).ToListAsync(token);
        var plan = new DedupePreparation(action.Id, action.LibraryId, action.ReviewKey, action.ScopeKey,
            files.Select(f => new DedupeFileSnapshot(f.MediaFileId, f.LibraryId, f.Root, f.Path, f.QuarantinePath,
                f.Identity, f.SizeBytes, f.ModifiedUtcTicks, f.Sha256, f.IsKeeper)).ToArray());
        if (action.Contract != DedupePreparationPolicy.Contract || DedupePreparationPolicy.Signature(plan) != action.PlanHash)
            throw new InvalidOperationException("This dedupe action's recorded plan is inconsistent, so its files were not touched.");
        return new(action.State, plan);
    }

    private async Task<DedupeActionContext> ContextAsync(DedupePreparation plan, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var ids = plan.Files.Select(f => f.MediaFileId).ToArray();
        var busy = await db.Jobs.AsNoTracking().Where(j => ids.Contains(j.MediaFileId)
                && j.Status != JobStatus.Completed && j.Status != JobStatus.Cancelled && j.Status != JobStatus.Failed)
            .Select(j => j.MediaFileId).ToListAsync(token);
        // A media manager tracks files by path and would report a moved copy missing. Asked now, after
        // the full reads, because a manager can pick a file up while those take minutes.
        var tracking = await scope.ServiceProvider.GetRequiredService<ArrTrackedFilesService>().GetAsync(token);
        // One file can have several inventory paths. The manager may track any of them, and the plan
        // only names one, so what it tracks counts for the physical file.
        var identities = plan.Files.Select(f => f.Identity).ToArray();
        var aliases = (await db.ExactDuplicateObservations.AsNoTracking()
                .Where(o => o.Identity != null && identities.Contains(o.Identity))
                .Select(o => new { o.MediaFileId, o.Identity }).Distinct().ToListAsync(token))
            .ToLookup(o => o.Identity!, o => o.MediaFileId);
        IReadOnlySet<int> Physical(IReadOnlyDictionary<int, IReadOnlyList<string>> byInventoryId) => plan.Files
            .Where(f => byInventoryId.ContainsKey(f.MediaFileId) || aliases[f.Identity].Any(byInventoryId.ContainsKey))
            .Select(f => f.MediaFileId).ToHashSet();
        return new(tracking.Complete, Physical(tracking.TrackedBy), Physical(tracking.PossiblyTrackedBy),
            !OperatingSystem.IsWindows(), busy.ToHashSet());
    }

    // Conditional on the state stored now, so a concurrent change is noticed rather than overwritten.
    private async Task<bool> TryTransitionAsync(Guid id, DedupeActionState from, DedupeActionState to, string? reason)
    {
        if (!DedupeJournalPolicy.CanTransition(from, to)) return false;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var now = DateTimeOffset.UtcNow;
        return await db.DedupeActions.Where(a => a.Id == id && a.State == from)
            .ExecuteUpdateAsync(set => set.SetProperty(a => a.State, to).SetProperty(a => a.AttentionReason, reason).SetProperty(a => a.UpdatedAt, now)) == 1;
    }

    private async Task TransitionAsync(Guid id, DedupeActionState to, string? reason)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var from = await db.DedupeActions.AsNoTracking().Where(a => a.Id == id).Select(a => a.State).SingleAsync();
        if (!await TryTransitionAsync(id, from, to, reason))
            throw new InvalidOperationException($"A dedupe action cannot move from {from} to {to}.");
    }
}
