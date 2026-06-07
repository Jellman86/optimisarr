using Microsoft.EntityFrameworkCore;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

public sealed record EnqueueResult(int Enqueued, int AlreadyQueued, int Ineligible);

/// <summary>
/// Creates queued <see cref="Job"/>s from a library's eligible candidates. Idempotent:
/// a media file that already has an active (non-terminal) job is not enqueued again,
/// so running it twice produces no duplicates.
/// </summary>
public sealed class JobEnqueueService(OptimisarrDbContext db, CandidateService candidates)
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
            return new EnqueueResult(0, 0, ineligible);
        }

        var eligibleIds = eligible.Select(candidate => candidate.MediaFileId).ToHashSet();
        var alreadyActive = (await db.Jobs
                .Where(job => eligibleIds.Contains(job.MediaFileId) && ActiveStatuses.Contains(job.Status))
                .Select(job => job.MediaFileId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var now = DateTimeOffset.UtcNow;
        var enqueued = 0;
        foreach (var candidate in eligible)
        {
            if (alreadyActive.Contains(candidate.MediaFileId))
            {
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
        return new EnqueueResult(enqueued, eligible.Count - enqueued, ineligible);
    }
}
