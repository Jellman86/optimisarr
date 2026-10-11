using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Optimisarr.Core.Library;
using Optimisarr.Data;
using Fingerprint = Optimisarr.Core.Library.ExactFileFingerprint;

namespace Optimisarr.Api.Library;

/// <summary>One retained snapshot per library, plus reusable completed file reads.</summary>
public sealed class ExactDuplicateStore(IServiceScopeFactory scopes)
{
    public const int PageSize = 20;

    public async Task RecoverAsync(CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await db.ExactDuplicateScans.Where(s => s.ActiveSlot != null).ExecuteUpdateAsync(setters => setters
            .SetProperty(s => s.State, ExactDuplicateScanState.Interrupted).SetProperty(s => s.ActiveSlot, (int?)null)
            .SetProperty(s => s.FinishedAt, DateTimeOffset.UtcNow)
            .SetProperty(s => s.Error, "The server stopped before this scan finished. Scan again to reuse completed file reads."), token);
    }

    public async Task<long?> TryCreateAsync(int libraryId, string root, int total, bool forceRefresh, CancellationToken token,
        IReadOnlyList<ExactDuplicateInput>? inputs = null, IReadOnlyList<ExactDuplicateLibrary>? libraries = null)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (await db.ExactDuplicateScans.AsNoTracking().AnyAsync(s => s.ActiveSlot != null, token)) return null;
        libraries ??= await db.Libraries.AsNoTracking().Where(l => l.Id == libraryId)
            .Select(l => new ExactDuplicateLibrary(l.Id, l.Name, l.Path, l.MediaType.ToString())).ToListAsync(token);
        if (libraries.Count is < 1 or > 16 || !libraries.Any(l => l.Id == libraryId)
            || libraries.Select(l => l.Id).Distinct().Count() != libraries.Count) return null;
        inputs ??= await ExactDuplicateInputs.ReadAsync(db, libraryId, token);
        if (inputs.Any(i => !libraries.Any(l => l.Id == (i.LibraryId == 0 ? libraryId : i.LibraryId)
            && (i.Root is null || Path.GetFullPath(i.Root) == Path.GetFullPath(l.Root))))) return null;
        var scopeInputs = inputs.Select(i => i with { LibraryId = i.LibraryId == 0 ? libraryId : i.LibraryId,
            Root = Path.GetFullPath(i.Root ?? libraries.Single(l => l.Id == (i.LibraryId == 0 ? libraryId : i.LibraryId)).Root) }).ToArray();
        await db.ExactDuplicateScans.Where(s => s.LibraryId == libraryId).ExecuteDeleteAsync(token);
        var scan = new ExactDuplicateScan { LibraryId = libraryId, Root = Path.GetFullPath(root), Total = total,
            StartedAt = DateTimeOffset.UtcNow, ScopeSignature = ExactDuplicateInputs.Signature(scopeInputs), State = ExactDuplicateScanState.Queued, ActiveSlot = 1, ForceRefresh = forceRefresh };
        db.ExactDuplicateScans.Add(scan);
        await db.SaveChangesAsync(token);
        db.ExactDuplicateScanLibraries.AddRange(libraries.Select(l => new ExactDuplicateScanLibrary
            { ScanId = scan.Id, LibraryId = l.Id, Root = Path.GetFullPath(l.Root), Name = l.Name, MediaType = l.MediaType }));
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return scan.Id;
    }

    public async Task<IReadOnlyDictionary<int, Fingerprint>> CacheAsync(IReadOnlyList<ExactDuplicateInput> inputs, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var cache = new Dictionary<int, Fingerprint>();
        foreach (var batch in inputs.Chunk(500))
        {
            var ids = batch.Select(i => i.Id).ToArray();
            foreach (var row in await db.ExactFileFingerprints.AsNoTracking().Where(f => ids.Contains(f.MediaFileId)).ToListAsync(token))
                cache[row.MediaFileId] = new(new(row.Root, row.Path, row.SizeBytes, row.ModifiedUtcTicks, row.Identity),
                    row.Sha256, row.CheckedAt, row.Contract);
        }
        return cache;
    }

    public async Task<bool> CheckpointAsync(long scanId, IReadOnlyList<ExactDuplicateCheckpoint> completed, CancellationToken token)
    {
        if (completed.Count == 0) return true;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var scan = await db.ExactDuplicateScans.SingleOrDefaultAsync(s => s.Id == scanId, token);
        if (scan is null) return false;
        var ids = completed.Select(c => c.Input.Id).ToArray();
        var existing = await db.ExactFileFingerprints.Where(f => ids.Contains(f.MediaFileId)).ToDictionaryAsync(f => f.MediaFileId, token);
        var recorded = await db.ExactDuplicateObservations.AsNoTracking().Where(o => o.ScanId == scanId && ids.Contains(o.MediaFileId))
            .Select(o => o.MediaFileId).ToListAsync(token);
        var libraryIds = await db.ExactDuplicateScanLibraries.AsNoTracking().Where(s => s.ScanId == scanId).Select(s => s.LibraryId).ToListAsync(token);
        var present = await db.MediaFiles.AsNoTracking().Where(f => ids.Contains(f.Id) && f.LibraryId != null && libraryIds.Contains(f.LibraryId.Value))
            .Select(f => f.Id).ToListAsync(token);
        foreach (var item in completed)
        {
            if (item.Fingerprint is not { } fingerprint)
            {
                if (existing.TryGetValue(item.Input.Id, out var stale)) db.ExactFileFingerprints.Remove(stale);
                continue;
            }
            if (!item.Reused && present.Contains(item.Input.Id))
            {
                if (!existing.TryGetValue(item.Input.Id, out var cached))
                {
                    cached = new() { MediaFileId = item.Input.Id };
                    db.ExactFileFingerprints.Add(cached);
                }
                cached.Root = fingerprint.Generation.Root; cached.Path = fingerprint.Generation.Path;
                cached.SizeBytes = fingerprint.Generation.SizeBytes; cached.ModifiedUtcTicks = fingerprint.Generation.ModifiedUtcTicks;
                cached.Identity = fingerprint.Generation.Identity; cached.Contract = fingerprint.Contract;
                cached.Sha256 = fingerprint.Sha256; cached.CheckedAt = fingerprint.CheckedAt;
            }
            if (recorded.Contains(item.Input.Id)) continue;
            db.ExactDuplicateObservations.Add(new() { ScanId = scanId, MediaFileId = item.Input.Id,
                Path = item.Input.Path, RelativePath = item.Input.RelativePath, SizeBytes = item.Input.SizeBytes,
                ModifiedUtcTicks = item.Input.ModifiedAt.UtcTicks, Sha256 = fingerprint.Sha256,
                HardLinkCount = item.HardLinkCount, CheckedAt = fingerprint.CheckedAt, Reused = item.Reused,
                LibraryId = item.Input.LibraryId == 0 ? scan.LibraryId : item.Input.LibraryId,
                Root = fingerprint.Generation.Root, Identity = fingerprint.Generation.Identity });
        }
        var progress = completed[^1].Progress;
        scan.Checked = progress.Checked; scan.Skipped = progress.Skipped; scan.Total = progress.Total;
        scan.BytesRead = progress.BytesRead; scan.Reused = progress.Reused;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return true;
    }

    public async Task SetStateAsync(long id, ExactDuplicateScanState state, CancellationToken token, string? error = null, ExactDuplicateProgress? progress = null)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        if (progress is not null)
            await db.ExactDuplicateScans.Where(s => s.Id == id).ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.Checked, progress.Checked).SetProperty(s => s.Skipped, progress.Skipped)
                .SetProperty(s => s.Total, progress.Total).SetProperty(s => s.BytesRead, progress.BytesRead)
                .SetProperty(s => s.Reused, progress.Reused), token);
        await db.ExactDuplicateScans.Where(s => s.Id == id).ExecuteUpdateAsync(setters => setters
            .SetProperty(s => s.State, state).SetProperty(s => s.Error, error)
            .SetProperty(s => s.ActiveSlot, state == ExactDuplicateScanState.Running ? 1 : (int?)null)
            .SetProperty(s => s.FinishedAt, state == ExactDuplicateScanState.Running ? null : (DateTimeOffset?)DateTimeOffset.UtcNow), token);
    }

    public async Task<ExactDuplicateStatus> ReadAsync(int libraryId, int page, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var scan = await db.ExactDuplicateScans.AsNoTracking().SingleOrDefaultAsync(s => s.LibraryId == libraryId, token);
        if (scan is null) return new(libraryId, "NotStarted", null, null, new(0, 0, 0, 0), null, null);
        var selected = await ScopeAsync(db, scan.Id, token);
        ExactDuplicateResult? result = null; var stale = false;
        if (scan.State == ExactDuplicateScanState.Completed)
        {
            var rows = await db.ExactDuplicateObservations.AsNoTracking().Where(f => f.ScanId == scan.Id).ToListAsync(token);
            stale = await StaleAsync(db, scan, selected, token);
            var groups = Groups(rows, selected);
            var bounded = ExactDuplicateGrouping.Limit(groups.Skip(page * PageSize).Take(PageSize)
                .Select(g => g with { CanDecide = g.ReviewKey is not null }).ToArray());
            var pageGroups = bounded.Groups;
            var visibleIds = pageGroups.SelectMany(g => g.Copies.SelectMany(c => new[] { c.Id }.Concat(c.Aliases?.Select(a => a.Id) ?? []))).ToHashSet();
            if (rows.Where(r => visibleIds.Contains(r.MediaFileId)).Any(r => !Matches(r))) stale = true;
            var scopeKey = ExactDuplicateReview.ScopeSignature(selected.Select(l => new ExactReviewScope(l.Id, l.Root)));
            var hashes = pageGroups.Select(g => g.Sha256).ToArray();
            var decisions = await db.ExactDuplicateReviewDecisions.AsNoTracking().Where(d => d.LibraryId == libraryId
                && d.ScopeKey == scopeKey && hashes.Contains(d.Sha256)).ToListAsync(token);
            var decorated = pageGroups.Select(g =>
            {
                var decision = decisions.SingleOrDefault(d => d.ReviewKey == g.ReviewKey && d.Sha256 == g.Sha256 && d.SizeBytes == g.SizeBytes);
                return g with { CanDecide = !stale && g.CanDecide,
                    Decision = !stale && decision is not null ? new(decision.Choice, JsonSerializer.Deserialize<int[]>(decision.KeepIds) ?? [], decision.SavedAt) : null };
            }).ToArray();
            result = new(scan.Checked, scan.Skipped, scan.Total, scan.BytesRead, decorated, bounded.Truncated,
                scan.Reused, groups.Count, page, (long)(page + 1) * PageSize < groups.Count);
        }
        return new(libraryId, scan.State.ToString(), scan.StartedAt, scan.FinishedAt,
            new(scan.Checked, scan.Skipped, scan.Total, scan.BytesRead, scan.Reused), result, scan.Error, stale, selected);
    }

    public async Task<bool> DecideAsync(int libraryId, string reviewKey, string choice, IReadOnlyList<int> keepIds, CancellationToken token)
    {
        if (reviewKey.Length != 64 || keepIds.Count > 100) return false;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var scan = await db.ExactDuplicateScans.AsNoTracking().SingleOrDefaultAsync(s => s.LibraryId == libraryId, token);
        if (scan is null || scan.State != ExactDuplicateScanState.Completed) return false;
        var selected = await ScopeAsync(db, scan.Id, token);
        if (await StaleAsync(db, scan, selected, token)) return false;
        var rows = await db.ExactDuplicateObservations.AsNoTracking().Where(f => f.ScanId == scan.Id).ToListAsync(token);
        var group = Groups(rows, selected).SingleOrDefault(g => g.ReviewKey == reviewKey);
        if (group is null || !ExactDuplicateReview.ValidSelection(group, choice, keepIds)) return false;
        if (ExactDuplicateGrouping.Limit([group]).Truncated) return false;
        var members = group.Copies.SelectMany(c => new[] { c.Id }.Concat(c.Aliases?.Select(a => a.Id) ?? [])).ToHashSet();
        if (rows.Where(r => members.Contains(r.MediaFileId)).Any(r => !Matches(r))) return false;
        var scopeKey = ExactDuplicateReview.ScopeSignature(selected.Select(l => new ExactReviewScope(l.Id, l.Root)));
        var row = await db.ExactDuplicateReviewDecisions.SingleOrDefaultAsync(d => d.LibraryId == libraryId
            && d.ScopeKey == scopeKey && d.Sha256 == group.Sha256 && d.SizeBytes == group.SizeBytes, token);
        if (choice == "Review")
        {
            if (row is not null) db.ExactDuplicateReviewDecisions.Remove(row);
        }
        else
        {
            var ids = JsonSerializer.Serialize(keepIds.Order().ToArray());
            if (row is null)
            {
                row = new() { LibraryId = libraryId, ScopeKey = scopeKey, Sha256 = group.Sha256, SizeBytes = group.SizeBytes };
                db.ExactDuplicateReviewDecisions.Add(row);
            }
            if (row.ReviewKey != reviewKey || row.Choice != choice || row.KeepIds != ids)
            {
                row.ReviewKey = reviewKey; row.Choice = choice; row.KeepIds = ids; row.SavedAt = DateTimeOffset.UtcNow;
            }
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return true;
    }

    private static Task<List<ExactDuplicateLibrary>> ScopeAsync(OptimisarrDbContext db, long scanId, CancellationToken token) =>
        db.ExactDuplicateScanLibraries.AsNoTracking().Where(s => s.ScanId == scanId).OrderBy(s => s.LibraryId)
            .Select(s => new ExactDuplicateLibrary(s.LibraryId, s.Name, s.Root, s.MediaType)).ToListAsync(token);

    private static async Task<bool> StaleAsync(OptimisarrDbContext db, ExactDuplicateScan scan, IReadOnlyList<ExactDuplicateLibrary> selected, CancellationToken token)
    {
        if (selected.Count == 0) return true;
        var ids = selected.Select(l => l.Id).ToArray();
        var current = await db.Libraries.AsNoTracking().Where(l => ids.Contains(l.Id)).Select(l => new { l.Id, l.Path }).ToListAsync(token);
        if (current.Count != ids.Length || current.Any(l => !selected.Any(s => s.Id == l.Id && s.Root == Path.GetFullPath(l.Path)))) return true;
        return ExactDuplicateInputs.Signature(await ExactDuplicateInputs.ReadAsync(db, ids, token)) != scan.ScopeSignature;
    }

    private static IReadOnlyList<ExactDuplicateGroup> Groups(IReadOnlyList<ExactDuplicateObservation> rows, IReadOnlyList<ExactDuplicateLibrary> selected)
    {
        var groups = ExactDuplicateGrouping.Group(rows.Select(f =>
        {
            var library = selected.SingleOrDefault(l => l.Id == f.LibraryId);
            var fingerprint = new Fingerprint(new(f.Root, Path.GetFullPath(f.Path), f.SizeBytes, f.ModifiedUtcTicks, f.Identity),
                f.Sha256, f.CheckedAt, ExactDuplicateCache.Contract);
            return new ExactDuplicateFile(f.MediaFileId, f.RelativePath, f.SizeBytes, f.Sha256, f.HardLinkCount, f.CheckedAt,
                f.Reused, f.LibraryId, library?.Name ?? "", library?.MediaType ?? "", f.Identity,
                f.Identity is not null ? ExactDuplicateReview.Generation(f.LibraryId, fingerprint) : null, Path.GetFullPath(f.Path));
        }));
        return groups.Select(g => g with { ReviewKey = ExactDuplicateReview.Signature(selected.Select(l => new ExactReviewScope(l.Id, l.Root)), g) }).ToArray();
    }

    private static bool Matches(ExactDuplicateObservation row)
    {
        try
        {
            var info = new FileInfo(row.Path);
            return row.Identity is not null && ExactDuplicateScanner.SafePath(row.Root, row.Path) && info.Exists
                && info.Length == row.SizeBytes && info.LastWriteTimeUtc.Ticks == row.ModifiedUtcTicks
                && ExactFileIdentity.Read(row.Path) == row.Identity;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}
