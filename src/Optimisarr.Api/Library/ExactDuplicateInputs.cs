using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Library;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

public static class ExactDuplicateInputs
{
    public const int MaximumCandidates = 50_000;

    public static Task<List<ExactDuplicateInput>> ReadAsync(OptimisarrDbContext db, int libraryId, CancellationToken token)
    {
        var sizes = db.MediaFiles.Where(f => f.LibraryId == libraryId && f.SizeBytes > 0)
            .GroupBy(f => f.SizeBytes).Where(g => g.Count() > 1).Select(g => g.Key);
        return db.MediaFiles.AsNoTracking().Where(f => f.LibraryId == libraryId && sizes.Contains(f.SizeBytes)
            && !db.Jobs.Any(j => j.MediaFileId == f.Id && j.Status != JobStatus.Completed
                && j.Status != JobStatus.Cancelled && j.Status != JobStatus.Failed))
            .OrderBy(f => f.Id).Select(f => new ExactDuplicateInput(f.Id, f.Path, f.RelativePath, f.SizeBytes, f.ModifiedAt))
            .Take(MaximumCandidates + 1).ToListAsync(token);
    }

    public static string Signature(IEnumerable<ExactDuplicateInput> files) => ExactDuplicateScope.Signature(
        files.Select(f => new ExactFileScopeEntry(f.Id, f.Path, f.RelativePath, f.SizeBytes, f.ModifiedAt.UtcTicks)));
}
