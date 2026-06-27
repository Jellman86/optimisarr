using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Queue;
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
    /// Lists jobs ordered highest priority first, then oldest enqueued first, optionally narrowed to
    /// a single <paramref name="status"/> (e.g. <see cref="JobStatus.Failed"/> for diagnostics).
    /// The ordering is applied after materialisation because SQLite cannot translate
    /// an ORDER BY over a <see cref="DateTimeOffset"/> column (<c>EnqueuedAt</c>).
    /// </summary>
    public static async Task<IReadOnlyList<JobDto>> ListAsync(
        OptimisarrDbContext db,
        CancellationToken cancellationToken,
        JobStatus? status = null)
    {
        var liveRollbackJobIds = (await db.Replacements
                .AsNoTracking()
                .Where(replacement => replacement.Status == ReplacementStatus.Replaced)
                .Select(replacement => replacement.JobId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var query = db.Jobs
            .AsNoTracking()
            // Previews are throwaway settings comparisons, surfaced in their own UI, not the queue.
            .Where(job => job.Type == JobType.Normal);

        if (status is { } wanted)
        {
            query = query.Where(job => job.Status == wanted);
        }

        var jobs = await query
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

    private const int FailureSamplesPerCategory = 5;

    /// <summary>
    /// Groups failed jobs by their classified <see cref="FailureCategory"/> with a count and a few
    /// recent samples each, so the diagnostics view can answer "why are jobs failing?" from the API
    /// without re-parsing every error message. Largest group first.
    /// </summary>
    public static async Task<IReadOnlyList<FailureGroupDto>> SummariseFailuresAsync(
        OptimisarrDbContext db,
        CancellationToken cancellationToken)
    {
        var failures = await db.Jobs
            .AsNoTracking()
            .Where(job => job.Type == JobType.Normal && job.Status == JobStatus.Failed)
            .Select(job => new
            {
                job.Id,
                job.MediaFileId,
                RelativePath = job.MediaFile != null ? job.MediaFile.RelativePath : null,
                job.ErrorMessage,
                job.FinishedAt
            })
            .ToListAsync(cancellationToken);

        return failures
            .GroupBy(job => FailureClassifier.Classify(job.ErrorMessage))
            .Select(group => new FailureGroupDto(
                group.Key.ToString(),
                FailureClassifier.Describe(group.Key),
                group.Count(),
                group
                    .OrderByDescending(job => job.FinishedAt)
                    .Take(FailureSamplesPerCategory)
                    .Select(job => new FailureSampleDto(job.Id, job.MediaFileId, job.RelativePath, job.ErrorMessage))
                    .ToList()))
            .OrderByDescending(group => group.Count)
            .ToList();
    }
}

/// <summary>One failed job shown as evidence under its category in the failure summary.</summary>
public sealed record FailureSampleDto(int JobId, int MediaFileId, string? RelativePath, string? ErrorMessage);

/// <summary>A classified group of failed jobs: a category, its description, a count, and samples.</summary>
public sealed record FailureGroupDto(
    string Category,
    string Description,
    int Count,
    IReadOnlyList<FailureSampleDto> Samples);
