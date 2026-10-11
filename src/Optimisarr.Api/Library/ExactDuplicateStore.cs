using Microsoft.EntityFrameworkCore;
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

    public async Task<long?> TryCreateAsync(int libraryId, string root, int total, bool forceRefresh, CancellationToken token, IReadOnlyList<ExactDuplicateInput>? inputs = null)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (await db.ExactDuplicateScans.AsNoTracking().AnyAsync(s => s.ActiveSlot != null, token)) return null;
        await db.ExactDuplicateScans.Where(s => s.LibraryId == libraryId).ExecuteDeleteAsync(token);
        inputs ??= await ExactDuplicateInputs.ReadAsync(db, libraryId, token);
        var scan = new ExactDuplicateScan { LibraryId = libraryId, Root = Path.GetFullPath(root), Total = total,
            StartedAt = DateTimeOffset.UtcNow, ScopeSignature = ExactDuplicateInputs.Signature(inputs), State = ExactDuplicateScanState.Queued, ActiveSlot = 1, ForceRefresh = forceRefresh };
        db.ExactDuplicateScans.Add(scan);
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
        var present = await db.MediaFiles.AsNoTracking().Where(f => ids.Contains(f.Id) && f.LibraryId == scan.LibraryId)
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
                HardLinkCount = item.HardLinkCount, CheckedAt = fingerprint.CheckedAt, Reused = item.Reused });
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
        ExactDuplicateResult? result = null; var stale = false;
        if (scan.State == ExactDuplicateScanState.Completed)
        {
            var rows = await db.ExactDuplicateObservations.AsNoTracking().Where(f => f.ScanId == scan.Id).ToListAsync(token);
            var groups = ExactDuplicateGrouping.Group(rows.Select(f => new ExactDuplicateFile(f.MediaFileId,
                f.RelativePath, f.SizeBytes, f.Sha256, f.HardLinkCount, f.CheckedAt, f.Reused)));
            var display = ExactDuplicateGrouping.Limit(groups.Skip(page * PageSize).Take(PageSize).ToArray());
            result = new(scan.Checked, scan.Skipped, scan.Total, scan.BytesRead, display.Groups, display.Truncated,
                scan.Reused, groups.Count, page, (long)(page + 1) * PageSize < groups.Count);
            var root = await db.Libraries.AsNoTracking().Where(l => l.Id == libraryId).Select(l => l.Path).SingleAsync(token);
            var current = await ExactDuplicateInputs.ReadAsync(db, libraryId, token);
            stale = Path.GetFullPath(root) != scan.Root || ExactDuplicateInputs.Signature(current) != scan.ScopeSignature;
        }
        return new(libraryId, scan.State.ToString(), scan.StartedAt, scan.FinishedAt,
            new(scan.Checked, scan.Skipped, scan.Total, scan.BytesRead, scan.Reused), result, scan.Error, stale);
    }
}
