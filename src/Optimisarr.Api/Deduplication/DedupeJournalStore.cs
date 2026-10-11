using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Deduplication;
using Optimisarr.Data;

namespace Optimisarr.Api.Deduplication;

public enum DedupePreparationResult { Created, AlreadyRecorded, Conflict, Invalid }

/// <summary>
/// Persists intent and reservations together. This store never moves files and never treats a
/// preparation, preference or cached fingerprint as authorisation for a filesystem action.
/// The future action adapter must independently qualify scope, aliases, storage, managers,
/// bundles and fresh content before invoking any move. This mechanical store is not preflight.
/// </summary>
public sealed class DedupeJournalStore(IServiceScopeFactory scopes, FileOperationClaims claims)
{
    public async Task<DedupePreparationResult> TryPrepareAsync(DedupePreparation plan, CancellationToken token)
    {
        plan = plan with { Files = plan.Files.ToArray() };
        if (!DedupePreparationPolicy.IsValid(plan) || !ValidPaths(plan)) return DedupePreparationResult.Invalid;
        var keys = Keys(plan.Files);
        using var held = claims.TryAcquire(keys);
        if (held is null) return DedupePreparationResult.Conflict;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var signature = DedupePreparationPolicy.Signature(plan);
        var previous = await db.DedupeActions.AsNoTracking().SingleOrDefaultAsync(a => a.Id == plan.Id, token);
        if (previous is not null)
        {
            if (previous.PlanHash != signature || previous.Contract != DedupePreparationPolicy.Contract
                || previous.Retention != DedupePreparationPolicy.Retention) return DedupePreparationResult.Conflict;
            if (DedupeJournalPolicy.HoldsClaims(previous.State))
            {
                var owned = await db.DedupeFileClaims.AsNoTracking().Where(c => c.ActionId == plan.Id).Select(c => c.Key).ToListAsync(token);
                if (!keys.All(owned.Contains)) return DedupePreparationResult.Conflict;
            }
            return DedupePreparationResult.AlreadyRecorded;
        }
        var ids = plan.Files.Select(f => f.MediaFileId).ToArray();
        var paths = plan.Files.Select(f => f.Path).ToArray();
        if (await IsReservedAsync(db, keys, token) || await db.Jobs.AsNoTracking().AnyAsync(j =>
            (ids.Contains(j.MediaFileId) || paths.Contains(j.MediaFile!.Path))
            && j.Status != JobStatus.Completed && j.Status != JobStatus.Cancelled && j.Status != JobStatus.Failed, token))
            return DedupePreparationResult.Conflict;
        var destinations = plan.Files.Where(f => f.QuarantinePath is not null).Select(f => f.QuarantinePath!).ToArray();
        if (await db.DedupeActionFiles.AsNoTracking().AnyAsync(f => f.QuarantinePath != null && destinations.Contains(f.QuarantinePath), token))
            return DedupePreparationResult.Conflict;
        var now = DateTimeOffset.UtcNow;
        db.DedupeActions.Add(new() { Id = plan.Id, LibraryId = plan.LibraryId, ReviewKey = plan.ReviewKey,
            ScopeKey = plan.ScopeKey, PlanHash = signature, CreatedAt = now, UpdatedAt = now });
        db.DedupeActionFiles.AddRange(plan.Files.Select(f => new DedupeActionFile { ActionId = plan.Id,
            MediaFileId = f.MediaFileId, LibraryId = f.LibraryId, Root = f.Root, Path = f.Path,
            QuarantinePath = f.QuarantinePath, Identity = f.Identity, Sha256 = f.Sha256,
            SizeBytes = f.SizeBytes, ModifiedUtcTicks = f.ModifiedUtcTicks, IsKeeper = f.IsKeeper }));
        db.DedupeFileClaims.AddRange(keys.Select(key => new DedupeFileClaim { Key = key, ActionId = plan.Id }));
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return DedupePreparationResult.Created;
    }

    public async Task<bool> CancelPreparedAsync(Guid id, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var action = await db.DedupeActions.SingleOrDefaultAsync(a => a.Id == id, token);
        if (action is null) return false;
        if (action.State == DedupeActionState.Cancelled) return true;
        if (!DedupeJournalPolicy.CanTransition(action.State, DedupeActionState.Cancelled)) return false;
        action.State = DedupeActionState.Cancelled;
        action.UpdatedAt = DateTimeOffset.UtcNow;
        await db.DedupeFileClaims.Where(c => c.ActionId == id).ExecuteDeleteAsync(token);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return true;
    }

    public async Task RecoverInterruptedAsync(CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var unresolved = await db.DedupeActions.Where(a => a.State != DedupeActionState.Restored && a.State != DedupeActionState.Cancelled).ToListAsync(token);
        foreach (var action in unresolved)
        {
            var files = await db.DedupeActionFiles.AsNoTracking().Where(f => f.ActionId == action.Id).ToListAsync(token);
            var plan = new DedupePreparation(action.Id, action.LibraryId, action.ReviewKey, action.ScopeKey,
                files.Select(f => new DedupeFileSnapshot(f.MediaFileId, f.LibraryId, f.Root, f.Path, f.QuarantinePath,
                    f.Identity, f.SizeBytes, f.ModifiedUtcTicks, f.Sha256, f.IsKeeper)).ToArray());
            if (action.Contract != DedupePreparationPolicy.Contract || action.Retention != DedupePreparationPolicy.Retention
                || !DedupePreparationPolicy.IsValid(plan) || !ValidPaths(plan) || DedupePreparationPolicy.Signature(plan) != action.PlanHash)
                throw new InvalidOperationException("An unresolved dedupe journal has unsupported or inconsistent rollback evidence. File operations cannot start safely.");
            foreach (var key in Keys(plan.Files))
            {
                var reservation = await db.DedupeFileClaims.AsNoTracking().SingleOrDefaultAsync(c => c.Key == key, token);
                if (reservation is not null && reservation.ActionId != action.Id)
                    throw new InvalidOperationException("Unresolved dedupe journals have conflicting file reservations. File operations cannot start safely.");
                if (reservation is null) db.DedupeFileClaims.Add(new() { Key = key, ActionId = action.Id });
            }
            var state = DedupeJournalPolicy.AfterRestart(action.State);
            if (state == action.State) continue;
            action.State = state;
            action.UpdatedAt = DateTimeOffset.UtcNow;
            action.AttentionReason = "The server stopped with unresolved file intent. All reservations remain held. File evidence must be checked before recovery.";
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    public static Task<bool> IsReservedAsync(OptimisarrDbContext db, IReadOnlyList<string> keys, CancellationToken token) =>
        db.DedupeFileClaims.AsNoTracking().AnyAsync(c => keys.Contains(c.Key), token);

    private static string[] Keys(IReadOnlyList<DedupeFileSnapshot> files) => files.SelectMany(f =>
        FileOperationClaims.SnapshotKeys(f.MediaFileId, f.Path, f.Identity).Concat(f.QuarantinePath is { } destination
            ? FileOperationClaims.SnapshotKeys(f.MediaFileId, destination, null) : []))
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static bool ValidPaths(DedupePreparation plan)
    {
        try
        {
            var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var sources = plan.Files.Select(f => f.Path).ToHashSet(comparer);
            var destinations = plan.Files.Where(f => !f.IsKeeper).Select(f => f.QuarantinePath!).ToArray();
            return sources.Count == plan.Files.Count && destinations.Distinct(comparer).Count() == destinations.Length
                && destinations.All(p => !sources.Contains(p)) && plan.Files.All(f =>
                    Canonical(f.Root) && Canonical(f.Path) && Within(f.Root, f.Path)
                    && (f.QuarantinePath is null || Canonical(f.QuarantinePath) && Within(f.Root, f.QuarantinePath)));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool Canonical(string path) => Path.IsPathFullyQualified(path) && Path.GetFullPath(path) == path;
    private static bool Within(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != "." && relative != ".." && !Path.IsPathRooted(relative)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}

/// <summary>Run before queue workers so unresolved reservations cannot be silently lost on restart.</summary>
public sealed class DedupeJournalRecovery(DedupeJournalStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => store.RecoverInterruptedAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
