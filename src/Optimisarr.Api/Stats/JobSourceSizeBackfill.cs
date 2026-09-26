using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Stats;
using Optimisarr.Data;

namespace Optimisarr.Api.Stats;

/// <summary>
/// One-time maintenance for jobs verified before <see cref="Job.SourceSizeBytes"/> existed: their
/// source size survives only in the stored report, so it is read from there once. Guarded by a
/// settings flag so it runs exactly once; jobs verified afterwards record the size directly.
/// </summary>
public static class JobSourceSizeBackfill
{
    public static async Task<int> FillFromReportsAsync(OptimisarrDbContext db, CancellationToken cancellationToken)
    {
        var alreadyRun = await db.AppSettings
            .AsNoTracking()
            .AnyAsync(setting => setting.Key == SettingKeys.JobSourceSizeBackfillDone, cancellationToken);
        if (alreadyRun)
        {
            return 0;
        }

        var legacy = await db.Jobs
            .Where(job => job.SourceSizeBytes == null && job.VerificationReportJson != null)
            .ToListAsync(cancellationToken);

        var filled = 0;
        foreach (var job in legacy)
        {
            if (LegacySizeEvidence.FromReportJson(job.VerificationReportJson).Sizes is { } sizes)
            {
                job.SourceSizeBytes = sizes.OriginalBytes;
                filled++;
            }
        }

        db.AppSettings.Add(new AppSetting { Key = SettingKeys.JobSourceSizeBackfillDone, Value = "1", UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
        return filled;
    }
}
