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
    public async Task<DedupeActionResult> QuarantineAsync(Guid id, CancellationToken token,
        TimeSpan? changeSettle = null, Action<DedupeFileSnapshot>? afterMove = null)
    {
        var loaded = await LoadAsync(id, token);
        if (loaded is not { } action) return DedupeActionResult.NotFound;
        if (action.State == DedupeActionState.Quarantined) return DedupeActionResult.Completed;
        if (action.State != DedupeActionState.Prepared)
            return DedupeActionResult.Refused($"This action is {action.State} and cannot start a move.");

        using var held = claims.TryAcquire(DedupeJournalStore.Keys(action.Plan.Files));
        if (held is null) return DedupeActionResult.Refused("Another file operation is using these files. Try again shortly.");

        var observations = new List<DedupeFileObservation>();
        foreach (var file in action.Plan.Files)
            observations.Add(await DedupeFileObserver.ObserveAsync(file, token, changeSettle));
        var issues = DedupeActionPreflight.Evaluate(action.Plan, observations, await ContextAsync(action.Plan, token)).Issues.ToList();
        var copies = action.Plan.Files.Where(f => !f.IsKeeper).ToArray();
        // Equal volume identifiers cannot prove a rename will work across bind mounts, so try one.
        if (issues.Count == 0)
            issues.AddRange(copies.Where(f => !FileMover.CanMoveAtomically(Path.GetDirectoryName(f.Path)!, Path.GetDirectoryName(f.QuarantinePath!)!))
                .Select(f => new DedupePreflightIssue(f.MediaFileId, DedupePreflightRefusal.CrossFilesystem)));
        if (issues.Count > 0)
        {
            RemoveEmptyQuarantineFolders(copies);
            return DedupeActionResult.Refused("The files cannot be moved safely.", issues.ToArray());
        }

        // Exceptions other than file errors deliberately leave this recorded: a stopped server
        // resolves it at restart through the journal, never by assuming how far the moves got.
        await TransitionAsync(id, DedupeActionState.Quarantining, null, token);
        try
        {
            foreach (var copy in copies)
            {
                token.ThrowIfCancellationRequested();
                if (action.Plan.Files.Where(f => f.IsKeeper).Append(copy).FirstOrDefault(f => !IsReviewedFile(f, f.Path)) is { } changed)
                    return await UndoAsync(id, action, $"{changed.Path} changed before the move.", token);
                Directory.CreateDirectory(Path.GetDirectoryName(copy.QuarantinePath!)!);
                var renamed = RenameOnly.Rename(copy.Path, copy.QuarantinePath!);
                if (renamed != RenameOutcome.Renamed)
                    return await UndoAsync(id, action, $"Could not move {copy.Path}: {renamed}.", token);
                afterMove?.Invoke(copy);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Quarantine of dedupe action {ActionId} failed", id);
            return await UndoAsync(id, action, ex.Message, CancellationToken.None);
        }

        await TransitionAsync(id, DedupeActionState.Quarantined, null, token);
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
        if (action.State is not (DedupeActionState.Quarantined or DedupeActionState.NeedsAttention))
            return DedupeActionResult.Refused($"This action is {action.State} and has nothing to restore.");

        using var held = claims.TryAcquire(DedupeJournalStore.Keys(action.Plan.Files));
        if (held is null) return DedupeActionResult.Refused("Another file operation is using these files. Try again shortly.");
        await TransitionAsync(id, DedupeActionState.Restoring, null, token);
        return await ReturnCopiesAsync(id, action, token);
    }

    private async Task<DedupeActionResult> UndoAsync(Guid id, LoadedAction action, string reason, CancellationToken token)
    {
        await TransitionAsync(id, DedupeActionState.NeedsAttention, reason, token);
        await TransitionAsync(id, DedupeActionState.Restoring, null, token);
        var undone = await ReturnCopiesAsync(id, action, token);
        return undone.Outcome == DedupeActionOutcome.Completed
            ? DedupeActionResult.Refused(reason + " Every file was returned to its original path.")
            : DedupeActionResult.Attention(reason + " " + undone.Reason);
    }

    private async Task<DedupeActionResult> ReturnCopiesAsync(Guid id, LoadedAction action, CancellationToken token)
    {
        var copies = action.Plan.Files.Where(f => !f.IsKeeper).ToArray();
        string? problem = null;
        foreach (var copy in copies)
        {
            try { problem = ReturnCopy(copy); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problem = $"Could not restore {copy.Path}: {ex.Message}"; }
            if (problem is not null) break;
        }
        if (problem is not null)
        {
            await TransitionAsync(id, DedupeActionState.NeedsAttention, problem, token);
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
    private static string? ReturnCopy(DedupeFileSnapshot copy)
    {
        var quarantine = copy.QuarantinePath!;
        var atQuarantine = Occupied(quarantine);
        var atOriginal = Occupied(copy.Path);
        if (!atQuarantine && atOriginal)
            return IsReviewedFile(copy, copy.Path) ? null : $"{copy.Path} is not the reviewed file.";
        if (!atQuarantine) return $"{copy.Path} is at neither its original path nor {quarantine}.";
        if (atOriginal) return $"{copy.Path} is occupied, so {quarantine} was left in place.";
        if (!IsReviewedFile(copy, quarantine)) return $"{quarantine} is not the reviewed file.";
        return RenameOnly.Rename(quarantine, copy.Path) switch
        {
            RenameOutcome.Renamed => null,
            var outcome => $"Could not restore {copy.Path}: {outcome}.",
        };
    }

    // A rename keeps the inode, size and modification time, so any difference means a different file.
    private static bool IsReviewedFile(DedupeFileSnapshot file, string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.LinkTarget is null && info.Length == file.SizeBytes && info.LastWriteTimeUtc.Ticks == file.ModifiedUtcTicks
            && ExactFileIdentity.Read(path) == file.Identity;
    }

    private static bool Occupied(string path) => Path.Exists(path) || new FileInfo(path).LinkTarget is not null;

    // Only empty folders this action's paths name, and only inside the quarantine folder.
    private static void RemoveEmptyQuarantineFolders(IEnumerable<DedupeFileSnapshot> copies)
    {
        foreach (var copy in copies)
        {
            var quarantineRoot = Path.Combine(copy.Root, DedupeQuarantinePaths.FolderName);
            for (var folder = Path.GetDirectoryName(copy.QuarantinePath!); folder is not null
                && folder.StartsWith(quarantineRoot, StringComparison.Ordinal); folder = Path.GetDirectoryName(folder))
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
        // A media manager tracks files by path and would re-import or mark a moved copy missing.
        // Connections are not scoped to a library, so any enabled one refuses for now.
        var managed = await db.ArrConnections.AsNoTracking().AnyAsync(c => c.Enabled, token);
        return new(managed, !OperatingSystem.IsWindows(), busy.ToHashSet());
    }

    private async Task TransitionAsync(Guid id, DedupeActionState to, string? reason, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var row = await db.DedupeActions.SingleAsync(a => a.Id == id, token);
        if (!DedupeJournalPolicy.CanTransition(row.State, to))
            throw new InvalidOperationException($"A dedupe action cannot move from {row.State} to {to}.");
        row.State = to;
        row.AttentionReason = reason;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token);
    }
}
