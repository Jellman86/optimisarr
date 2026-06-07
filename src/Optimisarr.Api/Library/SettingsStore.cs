using Microsoft.EntityFrameworkCore;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

/// <summary>Reads and writes well-known application settings in the database.</summary>
public sealed class SettingsStore(OptimisarrDbContext db)
{
    public async Task<string?> GetLibraryRootAsync(CancellationToken cancellationToken)
    {
        var setting = await db.AppSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == SettingKeys.LibraryRoot, cancellationToken);

        return setting?.Value;
    }

    public async Task SetLibraryRootAsync(string path, CancellationToken cancellationToken)
    {
        var existing = await db.AppSettings
            .FirstOrDefaultAsync(s => s.Key == SettingKeys.LibraryRoot, cancellationToken);

        if (existing is null)
        {
            db.AppSettings.Add(new AppSetting
            {
                Key = SettingKeys.LibraryRoot,
                Value = path,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            existing.Value = path;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
