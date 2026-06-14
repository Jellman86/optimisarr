using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

/// <summary>
/// One-time maintenance for databases created before media-kind classification existed: those
/// rows were stored with <see cref="MediaKind.Unknown"/> and the idempotent scan never revisits
/// an unchanged file, so an actual video/audio/image stays misclassified — which sends it down the
/// wrong pipeline. This resets such already-probed files to <see cref="MediaFileStatus.Discovered"/>
/// so the normal probe worker re-probes and re-classifies them. Guarded by a settings flag so it
/// runs exactly once.
/// </summary>
public static class MediaKindBackfill
{
    public static async Task<int> ResetUnknownProbedFilesAsync(
        OptimisarrDbContext db, CancellationToken cancellationToken)
    {
        var alreadyRun = await db.AppSettings
            .AsNoTracking()
            .AnyAsync(setting => setting.Key == SettingKeys.MediaKindBackfillDone, cancellationToken);
        if (alreadyRun)
        {
            return 0;
        }

        var stale = await db.MediaFiles
            .Where(file => file.Status == MediaFileStatus.Probed && file.MediaKind == MediaKind.Unknown)
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        foreach (var file in stale)
        {
            file.Status = MediaFileStatus.Discovered;
            file.UpdatedAt = now;
        }

        db.AppSettings.Add(new AppSetting { Key = SettingKeys.MediaKindBackfillDone, Value = "1", UpdatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        return stale.Count;
    }
}
