using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Activity;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

public sealed record EnqueueResult(int Enqueued, int AlreadyQueued, int Ineligible, int Importing);

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
        JobStatus.ReadyToReplace
    ];

    public async Task<EnqueueResult> EnqueueEligibleAsync(Data.Library library, CancellationToken cancellationToken)
    {
        var evaluated = await candidates.EvaluateAsync(library.Id, cancellationToken);
        var eligible = evaluated.Where(candidate => candidate.Eligible).ToList();
        var ineligible = evaluated.Count - eligible.Count;

        if (eligible.Count == 0)
        {
            return new EnqueueResult(0, 0, ineligible, 0);
        }

        var eligibleIds = eligible.Select(candidate => candidate.MediaFileId).ToHashSet();
        var alreadyActive = (await db.Jobs
                .Where(job => eligibleIds.Contains(job.MediaFileId) && ActiveStatuses.Contains(job.Status))
                .Select(job => job.MediaFileId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var activeImports = await arrActivity.GetActiveImportsAsync(cancellationToken);
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
                EnqueuedAt = now,
                UpdatedAt = now
            });
            enqueued++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new EnqueueResult(enqueued, eligible.Count - enqueued - importing, ineligible, importing);
    }
}
