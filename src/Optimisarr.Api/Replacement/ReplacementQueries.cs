using Microsoft.EntityFrameworkCore;
using Optimisarr.Data;
using ReplacementEntity = Optimisarr.Data.Replacement;

namespace Optimisarr.Api.Replacement;

/// <summary>A replacement row shaped for the Quarantine UI.</summary>
public sealed record ReplacementDto(
    int Id,
    int JobId,
    int MediaFileId,
    string OriginalPath,
    string QuarantinePath,
    string FinalPath,
    long OriginalSizeBytes,
    long NewSizeBytes,
    bool CrossFilesystem,
    string Status,
    DateTimeOffset ReplacedAt,
    DateTimeOffset? RolledBackAt,
    DateTimeOffset? PurgedAt)
{
    public static ReplacementDto From(ReplacementEntity replacement) => new(
        replacement.Id,
        replacement.JobId,
        replacement.MediaFileId,
        replacement.OriginalPath,
        replacement.QuarantinePath,
        replacement.FinalPath,
        replacement.OriginalSizeBytes,
        replacement.NewSizeBytes,
        replacement.CrossFilesystem,
        replacement.Status.ToString(),
        replacement.ReplacedAt,
        replacement.RolledBackAt,
        replacement.PurgedAt);
}

public static class ReplacementQueries
{
    /// <summary>
    /// Lists replacements newest first. The ordering is applied after materialisation
    /// because SQLite cannot translate an ORDER BY over a <see cref="DateTimeOffset"/>.
    /// </summary>
    public static async Task<IReadOnlyList<ReplacementDto>> ListAsync(
        OptimisarrDbContext db,
        CancellationToken cancellationToken)
    {
        var rows = await db.Replacements
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return rows
            .OrderByDescending(replacement => replacement.ReplacedAt)
            .Select(ReplacementDto.From)
            .ToList();
    }
}
