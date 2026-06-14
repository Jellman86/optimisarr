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

/// <summary>
/// A single replacement plus the verification evidence from its job, for the Quarantine
/// compare-to-approve view. The report JSON is the same one the Queue page renders.
/// </summary>
public sealed record ReplacementDetailDto(
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
    DateTimeOffset? PurgedAt,
    string MediaKind,
    bool? VerificationPassed,
    string? VerificationReportJson);

public static class ReplacementQueries
{
    /// <summary>One replacement with its job's verification report, or null if not found.</summary>
    public static async Task<ReplacementDetailDto?> GetAsync(
        OptimisarrDbContext db,
        int id,
        CancellationToken cancellationToken)
    {
        var replacement = await db.Replacements
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (replacement is null)
        {
            return null;
        }

        var job = await db.Jobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == replacement.JobId, cancellationToken);

        // The media kind decides which compare viewer (image/video/audio) the Quarantine UI shows.
        var mediaKind = await db.MediaFiles
            .AsNoTracking()
            .Where(file => file.Id == replacement.MediaFileId)
            .Select(file => file.MediaKind)
            .FirstOrDefaultAsync(cancellationToken);

        return new ReplacementDetailDto(
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
            replacement.PurgedAt,
            mediaKind.ToString(),
            job?.VerificationPassed,
            job?.VerificationReportJson);
    }

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
