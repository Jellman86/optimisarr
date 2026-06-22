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
    string? VideoEncoder,
    long? OutputSizeBytes,
    bool? VerificationPassed,
    string? VerificationReportJson,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    bool Clearable);

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
        var liveRollbackJobIds = (await db.Replacements
                .AsNoTracking()
                .Where(replacement => replacement.Status == ReplacementStatus.Replaced)
                .Select(replacement => replacement.JobId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var jobs = await db.Jobs
            .AsNoTracking()
            // Previews are throwaway settings comparisons, surfaced in their own UI, not the queue.
            .Where(job => job.Type == JobType.Normal)
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
                job.VideoEncoder,
                job.OutputSizeBytes,
                job.VerificationPassed,
                job.VerificationReportJson,
                job.VerifiedAt,
                job.EnqueuedAt,
                job.StartedAt,
                job.FinishedAt,
                false))
            .ToListAsync(cancellationToken);

        return jobs
            .Select(job => job with
            {
                Clearable = JobClearing.IsClearable(
                    new Job { Id = job.Id, Status = Enum.Parse<JobStatus>(job.Status) },
                    liveRollbackJobIds)
            })
            .OrderByDescending(job => job.Priority)
            .ThenBy(job => job.EnqueuedAt)
            .ToList();
    }
}
