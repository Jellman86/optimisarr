using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Library;
using Optimisarr.Core.Activity;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

public sealed record EnqueueResult(int Enqueued, int AlreadyQueued, int Ineligible, int Importing);

public enum FileEnqueueOutcome { Enqueued, AlreadyActive, NotFound, NeedsProbe, Ineligible, Importing }

public sealed record FileEnqueueResult(FileEnqueueOutcome Outcome, int? JobId, string? Reason);

/// <summary>
/// Creates queued <see cref="Job"/>s from a library's eligible candidates. Idempotent:
/// a media file that already has an active (non-terminal) job is not enqueued again,
/// so running it twice produces no duplicates. A file whose folder a connected
/// Sonarr/Radarr is currently importing into is held back so a transcode never fights
/// an import; it becomes eligible again on the next enqueue once the import settles.
/// </summary>
public sealed class JobEnqueueService(OptimisarrDbContext db, CandidateService candidates, ArrActivityService arrActivity)
{
    /// <summary>Statuses that mean a job is still in flight and must not be duplicated.</summary>
    public static readonly JobStatus[] ActiveStatuses =
    [
        JobStatus.Queued,
        JobStatus.Probing,
        JobStatus.Transcoding,
        JobStatus.Verifying,
        JobStatus.Leased,
        JobStatus.AwaitingVerification,
        JobStatus.AwaitingSizeReview,
        JobStatus.ReadyToReplace
    ];

    public async Task<EnqueueResult> EnqueueEligibleAsync(Data.Library library, CancellationToken cancellationToken)
    {
        // Fetch network activity before reserving SQLite's writer. The inventory/reservation
        // decision and the enqueue commit must share the subsequent transaction.
        var activeImports = await arrActivity.GetActiveImportsAsync(cancellationToken);
        await using var write = await db.BeginDiagnosticWriteAsync(cancellationToken);
        var evaluated = await candidates.EvaluateAsync(library.Id, cancellationToken);
        var eligible = evaluated.Where(candidate => candidate.Eligible).ToList();
        var ineligible = evaluated.Count - eligible.Count;

        if (eligible.Count == 0)
        {
            return new EnqueueResult(0, 0, ineligible, 0);
        }

        var eligibleIds = eligible.Select(candidate => candidate.MediaFileId).ToHashSet();
        var alreadyActive = (await db.Jobs
                .Where(job => job.Type == JobType.Normal
                    && eligibleIds.Contains(job.MediaFileId)
                    && ActiveStatuses.Contains(job.Status))
                .Select(job => job.MediaFileId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var paths = await db.MediaFiles
            .Where(file => eligibleIds.Contains(file.Id))
            .Select(file => new { file.Id, file.Path })
            .ToDictionaryAsync(file => file.Id, file => file.Path, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var enqueued = 0;
        var importing = 0;
        foreach (var candidate in eligible)
        {
            if (alreadyActive.Contains(candidate.MediaFileId))
            {
                continue;
            }

            if (paths.TryGetValue(candidate.MediaFileId, out var path)
                && ArrImportExclusionEvaluator.ExclusionReason(path, activeImports) is not null)
            {
                importing++;
                continue;
            }

            db.Jobs.Add(new Job
            {
                MediaFileId = candidate.MediaFileId,
                LibraryId = candidate.LibraryId,
                Status = JobStatus.Queued,
                Priority = library.Priority,
                EnqueueReason = Truncate(candidate.Reason),
                EnqueuedAt = now,
                UpdatedAt = now
            });
            enqueued++;
        }

        await db.SaveChangesAsync(cancellationToken);
        await write.CommitAsync(cancellationToken);
        return new EnqueueResult(enqueued, eligible.Count - enqueued - importing, ineligible, importing);
    }

    /// <summary>
    /// Queues one file the operator chose. Choosing it only overrides "optimise automatically" being
    /// off: every eligibility rule, exclusion and reservation still applies, and scheduling, playback
    /// holds and capacity still decide when it runs. A previously failed file is retried from its own
    /// job, so its attempt history stays together. Evaluated inside the writer reservation so
    /// concurrent requests cannot both add a job.
    /// </summary>
    public async Task<FileEnqueueResult> EnqueueFileAsync(int mediaFileId, CancellationToken cancellationToken)
    {
        var activeImports = await arrActivity.GetActiveImportsAsync(cancellationToken);
        await using var write = await db.BeginDiagnosticWriteAsync(cancellationToken);
        var file = await db.MediaFiles.AsNoTracking().Include(f => f.Library)
            .SingleOrDefaultAsync(f => f.Id == mediaFileId, cancellationToken);
        if (file is null) return new(FileEnqueueOutcome.NotFound, null, null);

        var active = await db.Jobs.AsNoTracking()
            .Where(job => job.Type == JobType.Normal && job.MediaFileId == mediaFileId && ActiveStatuses.Contains(job.Status))
            .Select(job => (int?)job.Id).FirstOrDefaultAsync(cancellationToken);
        if (active is not null) return new(FileEnqueueOutcome.AlreadyActive, active, null);

        if (file.Status != MediaFileStatus.Probed)
            return new(FileEnqueueOutcome.NeedsProbe, null, "Probe this file before queueing it.");
        // A stale inventory row is the more useful answer, so it comes before the path check,
        // which also fails for a file that is simply gone.
        var info = new FileInfo(file.Path);
        if (!info.Exists || info.Length != file.SizeBytes || info.LastWriteTimeUtc != file.ModifiedAt.UtcDateTime)
            return new(FileEnqueueOutcome.NeedsProbe, null, "This file has changed since it was scanned. Scan and probe it again.");
        if (!LibraryPathSafety.IsSafeFile(file.Path, file.Library?.Path))
            return new(FileEnqueueOutcome.Ineligible, null, LibraryPathSafety.Refusal);

        var candidate = (await candidates.EvaluateAsync(file.LibraryId, cancellationToken))
            .FirstOrDefault(c => c.MediaFileId == mediaFileId);
        if (candidate is not { Eligible: true })
            return new(FileEnqueueOutcome.Ineligible, null, candidate?.Reason ?? "This file can't be optimised.");
        if (ArrImportExclusionEvaluator.ExclusionReason(file.Path, activeImports) is { } importing)
            return new(FileEnqueueOutcome.Importing, null, importing);

        var now = DateTimeOffset.UtcNow;
        var added = new Job
        {
            MediaFileId = mediaFileId,
            LibraryId = file.LibraryId,
            Status = JobStatus.Queued,
            Priority = file.Library?.Priority ?? 0,
            EnqueueReason = Truncate("Queued manually: " + candidate.Reason),
            EnqueuedAt = now,
            UpdatedAt = now,
        };
        db.Jobs.Add(added);
        await db.SaveChangesAsync(cancellationToken);
        await write.CommitAsync(cancellationToken);
        return new(FileEnqueueOutcome.Enqueued, added.Id, null);
    }

    // The reason column is bounded; an overlong reason keeps its head, which carries
    // the meaningful part ("Remove N audio track(s) (…").
    private static string? Truncate(string? reason) =>
        reason is { Length: > 512 } ? reason[..512] : reason;
}
