using Microsoft.EntityFrameworkCore;
using Optimisarr.Data;

namespace Optimisarr.Api.Queue;

/// <summary>A single job row shaped for the queue UI.</summary>
public sealed record JobDto(
    int Id,
    int MediaFileId,
    int? LibraryId,
    string? RelativePath,
    string Status,
    int Priority,
    double Progress,
    string? ErrorMessage,
    string? FfmpegArguments,
    long? OutputSizeBytes,
    bool? VerificationPassed,
    string? VerificationReportJson,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public static class JobQueries
{
    /// <summary>
    /// Lists all jobs ordered highest priority first, then oldest enqueued first.
    /// The ordering is applied after materialisation because SQLite cannot translate
    /// an ORDER BY over a <see cref="DateTimeOffset"/> column (<c>EnqueuedAt</c>).
    /// </summary>
    public static async Task<IReadOnlyList<JobDto>> ListAsync(
        OptimisarrDbContext db,
        CancellationToken cancellationToken)
    {
        var jobs = await db.Jobs
            .AsNoTracking()
            .Select(job => new JobDto(
                job.Id,
                job.MediaFileId,
                job.LibraryId,
                job.MediaFile != null ? job.MediaFile.RelativePath : null,
                job.Status.ToString(),
                job.Priority,
                job.Progress,
                job.ErrorMessage,
                job.FfmpegArguments,
                job.OutputSizeBytes,
                job.VerificationPassed,
                job.VerificationReportJson,
                job.VerifiedAt,
                job.EnqueuedAt,
                job.StartedAt,
                job.FinishedAt))
            .ToListAsync(cancellationToken);

        return jobs
            .OrderByDescending(job => job.Priority)
            .ThenBy(job => job.EnqueuedAt)
            .ToList();
    }
}
