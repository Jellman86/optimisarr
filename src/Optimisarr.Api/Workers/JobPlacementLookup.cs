using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Queue;
using Optimisarr.Data;

namespace Optimisarr.Api.Workers;

/// <summary>
/// Reads the placement a job's library asks for, keeping "the library says Anywhere" apart from
/// "this job, its file, or its library could not be resolved".
///
/// <para>The enum's default is itself a real placement, so a projection that is not nullable
/// reports a vanished job as <see cref="WorkPlacement.Anywhere"/>: a decision that never found a
/// library looks exactly like one that found it and was told to run here. Every placement question
/// asked of the database goes through here so that distinction is made in one place.</para>
/// </summary>
internal static class JobPlacementLookup
{
    public static async Task<WorkPlacement?> ForJobAsync(
        OptimisarrDbContext db,
        int jobId,
        CancellationToken cancellationToken)
        => await db.Jobs
            .AsNoTracking()
            .Where(job => job.Id == jobId && job.Type == JobType.Normal)
            .Select(job => (WorkPlacement?)job.MediaFile!.Library!.WorkPlacement)
            .FirstOrDefaultAsync(cancellationToken);
}
