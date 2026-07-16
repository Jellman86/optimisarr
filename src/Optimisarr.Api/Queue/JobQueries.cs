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
    string? EnqueueReason,
    string? FailureCategory,
    string? FfmpegArguments,
    string? VideoEncoder,
    int? RequestedVideoQuality,
    int? EffectiveVideoQuality,
    string? VideoQualityMode,
    int QualityRetryCount,
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
    /// a single <paramref name="status"/>. Thin wrapper over <see cref="QueryAsync"/> for the queue
    /// feed, which wants every matching job (no paging).
    /// </summary>
    public static async Task<IReadOnlyList<JobDto>> ListAsync(
        OptimisarrDbContext db,
        CancellationToken cancellationToken,
        JobStatus? status = null) =>
        (await QueryAsync(db, new JobQuery { Status = status }, cancellationToken)).Items;

    /// <summary>
    /// Filtered, optionally paged job query for the queue feed and diagnostics. SQL-translatable
    /// filters (status, library, failure category) run in the database; the date filter, ordering, and
    /// paging run in memory because SQLite cannot translate an ORDER BY or comparison over a
    /// <see cref="DateTimeOffset"/> column. <see cref="JobQueryResult.Total"/> is the match count
    /// before paging, so a caller can show "page N of M".
    /// </summary>
    public static async Task<JobQueryResult> QueryAsync(
        OptimisarrDbContext db,
        JobQuery filter,
        CancellationToken cancellationToken)
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

        if (filter.Status is { } status)
        {
            query = query.Where(job => job.Status == status);
        }
        if (filter.LibraryId is { } libraryId)
        {
            query = query.Where(job => job.LibraryId == libraryId);
        }
        if (filter.Category is { } category)
        {
            query = query.Where(job => job.FailureCategory == category);
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
                job.EnqueueReason,
                job.FailureCategory != null ? job.FailureCategory.ToString() : null,
                job.FfmpegArguments,
                job.VideoEncoder,
                job.RequestedVideoQuality,
                job.EffectiveVideoQuality,
                job.VideoQualityMode,
                job.QualityRetryCount,
                job.OutputSizeBytes,
                job.VerificationPassed,
                job.VerificationReportJson,
                job.VerifiedAt,
                job.EnqueuedAt,
                job.StartedAt,
                job.FinishedAt,
                false))
            .ToListAsync(cancellationToken);

        var ordered = jobs
            .Select(job => job with
            {
                Clearable = JobClearing.IsClearable(
                    new Job { Id = job.Id, Status = Enum.Parse<JobStatus>(job.Status) },
                    liveRollbackJobIds)
            })
            // A job's effective time is when it finished, or when it was enqueued if it hasn't.
            .Where(job => WithinRange(job.FinishedAt ?? job.EnqueuedAt, filter.Since, filter.Until))
            .OrderByDescending(job => job.Priority)
            .ThenBy(job => job.EnqueuedAt)
            .ToList();

        var page = filter.PageSize > 0
            ? ordered.Skip(Math.Max(filter.Page - 1, 0) * filter.PageSize).Take(filter.PageSize).ToList()
            : ordered;

        return new JobQueryResult(page, ordered.Count);
    }

    private static bool WithinRange(DateTimeOffset value, DateTimeOffset? since, DateTimeOffset? until) =>
        (since is not { } from || value >= from) && (until is not { } to || value <= to);

    private const int FailureSamplesPerCategory = 5;

    /// <summary>
    /// Groups failed jobs by their classified <see cref="FailureCategory"/> with a count and a few
    /// recent samples each, so the diagnostics view can answer "why are jobs failing?" from the API
    /// without re-parsing every error message. Largest group first. Optionally narrowed to one
    /// library.
    /// </summary>
    public static async Task<IReadOnlyList<FailureGroupDto>> SummariseFailuresAsync(
        OptimisarrDbContext db,
        CancellationToken cancellationToken,
        int? libraryId = null)
    {
        var query = db.Jobs
            .AsNoTracking()
            .Where(job => job.Type == JobType.Normal && job.Status == JobStatus.Failed);

        if (libraryId is { } id)
        {
            query = query.Where(job => job.LibraryId == id);
        }

        var failures = await query
            .Select(job => new
            {
                job.Id,
                job.MediaFileId,
                RelativePath = job.MediaFile != null ? job.MediaFile.RelativePath : null,
                job.ErrorMessage,
                job.FailureCategory,
                job.FinishedAt
            })
            .ToListAsync(cancellationToken);

        return failures
            // Prefer the category stored when the job failed; fall back to classifying the message for
            // rows that failed before the category was persisted.
            .GroupBy(job => job.FailureCategory ?? FailureClassifier.Classify(job.ErrorMessage))
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

/// <summary>
/// Filters and paging for <see cref="JobQueries.QueryAsync"/>. All filters are optional; the date
/// range is matched against a job's finished time (or its enqueued time if it hasn't finished).
/// <see cref="PageSize"/> of 0 disables paging and returns every match.
/// </summary>
public sealed record JobQuery
{
    public JobStatus? Status { get; init; }
    public int? LibraryId { get; init; }
    public FailureCategory? Category { get; init; }
    public DateTimeOffset? Since { get; init; }
    public DateTimeOffset? Until { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; }
}

/// <summary>A page of jobs plus the total number of matches before paging.</summary>
public sealed record JobQueryResult(IReadOnlyList<JobDto> Items, int Total);
