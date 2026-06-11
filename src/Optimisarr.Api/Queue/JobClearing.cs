using Optimisarr.Data;

namespace Optimisarr.Api.Queue;

/// <summary>
/// Decides which finished jobs the Queue's "Clear" action may delete. A job is clearable
/// only when it is terminal <em>and</em> not protected by a still-reversible replacement:
/// a job whose original is still in quarantine is the live rollback path and must never be
/// removed (the safety standard requires every destructive step keep its rollback). Failed
/// and cancelled jobs never replaced anything, so they are always clearable; a completed
/// job becomes clearable once its replacement has been rolled back or purged.
/// </summary>
public static class JobClearing
{
    public static readonly IReadOnlySet<JobStatus> TerminalStatuses =
        new HashSet<JobStatus> { JobStatus.Completed, JobStatus.Failed, JobStatus.Cancelled };

    public static bool IsClearable(Job job, ISet<int> jobIdsWithLiveRollback) =>
        TerminalStatuses.Contains(job.Status) && !jobIdsWithLiveRollback.Contains(job.Id);
}
