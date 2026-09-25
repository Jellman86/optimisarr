using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Stats;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Api.Stats;

/// <summary>One finished, verified optimisation for the Dashboard's recent results.</summary>
public sealed record ResultDto(
    int JobId,
    int MediaFileId,
    string? RelativePath,
    int? LibraryId,
    string? LibraryName,
    long SourceSizeBytes,
    long OutputSizeBytes,
    double? VmafHarmonicMean,
    string? VideoEncoder,
    string? WorkerName,
    DateTimeOffset FinishedAt);

public sealed record DailySavingDto(DateOnly Date, long BytesSaved, int Files);

/// <summary>
/// Outcomes read from job history rather than replacement rows: quarantine entries can be cleared,
/// completed jobs stay until the operator clears the queue history.
/// </summary>
public static class ResultsQueries
{
    public const int MaxRecent = 100;
    public const int MaxDays = 366;

    private static IQueryable<Job> Finished(OptimisarrDbContext db) => db.Jobs
        .AsNoTracking()
        .Where(job => job.Type == JobType.Normal
            && job.Status == JobStatus.Completed
            && job.SourceSizeBytes != null
            && job.OutputSizeBytes != null
            && job.FinishedAt != null);

    public static async Task<IReadOnlyList<ResultDto>> RecentAsync(
        OptimisarrDbContext db, int take, CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, MaxRecent);
        // SQLite cannot ORDER BY a DateTimeOffset column, so the newest ids are picked in memory
        // from a narrow projection before the reports are read.
        var newest = (await Finished(db)
                .Select(job => new { job.Id, job.FinishedAt })
                .ToListAsync(cancellationToken))
            .OrderByDescending(job => job.FinishedAt)
            .Take(take)
            .Select(job => job.Id)
            .ToList();

        var rows = await Finished(db)
            .Where(job => newest.Contains(job.Id))
            .Select(job => new
            {
                job.Id,
                job.MediaFileId,
                RelativePath = job.MediaFile != null ? job.MediaFile.RelativePath : null,
                LibraryId = job.MediaFile != null ? job.MediaFile.LibraryId : job.LibraryId,
                LibraryName = job.MediaFile != null && job.MediaFile.Library != null ? job.MediaFile.Library.Name : null,
                SourceSizeBytes = job.SourceSizeBytes!.Value,
                OutputSizeBytes = job.OutputSizeBytes!.Value,
                job.VerificationReportJson,
                job.VideoEncoder,
                job.StartedAt,
                FinishedAt = job.FinishedAt!.Value,
            })
            .ToListAsync(cancellationToken);

        var workers = await WorkerNamesAsync(db, rows.ToDictionary(row => row.Id, row => row.StartedAt), cancellationToken);
        return rows
            .OrderByDescending(row => row.FinishedAt)
            .Select(row => new ResultDto(
                row.Id,
                row.MediaFileId,
                row.RelativePath,
                row.LibraryId,
                row.LibraryName,
                row.SourceSizeBytes,
                row.OutputSizeBytes,
                LegacySizeEvidence.FromReportJson(row.VerificationReportJson).VmafHarmonicMean,
                row.VideoEncoder,
                workers.GetValueOrDefault(row.Id),
                row.FinishedAt))
            .ToList();
    }

    public static async Task<IReadOnlyList<DailySavingDto>> DailyAsync(
        OptimisarrDbContext db, int days, DateTimeOffset nowUtc, TimeSpan utcOffset, CancellationToken cancellationToken)
    {
        days = Math.Clamp(days, 1, MaxDays);
        var entries = await Finished(db)
            .Select(job => new SavingsEntry(job.FinishedAt!.Value, job.SourceSizeBytes!.Value, job.OutputSizeBytes!.Value))
            .ToListAsync(cancellationToken);
        return DailySavings.Bucket(entries, nowUtc, days, utcOffset)
            .Select(day => new DailySavingDto(day.Date, day.BytesSaved, day.Files))
            .ToList();
    }

    // The worker that produced the delivered output: the latest completed lease taken during the
    // job's final attempt. A local retry after a remote attempt belongs to this server.
    private static async Task<Dictionary<int, string?>> WorkerNamesAsync(
        OptimisarrDbContext db, IReadOnlyDictionary<int, DateTimeOffset?> startedAt, CancellationToken cancellationToken)
    {
        var ids = startedAt.Keys.ToList();
        var leases = await db.JobLeases
            .AsNoTracking()
            .Where(lease => ids.Contains(lease.JobId) && lease.State == LeaseState.Completed)
            .Select(lease => new { lease.JobId, lease.AcquiredAt, WorkerName = lease.Worker != null ? lease.Worker.Name : null })
            .ToListAsync(cancellationToken);
        return leases
            .Where(lease => startedAt[lease.JobId] is not { } start || lease.AcquiredAt >= start.AddSeconds(-1))
            .GroupBy(lease => lease.JobId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(lease => lease.AcquiredAt).First().WorkerName);
    }
}
