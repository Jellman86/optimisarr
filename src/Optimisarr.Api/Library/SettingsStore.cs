using Microsoft.EntityFrameworkCore;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

/// <summary>Reads well-known application settings from the database.</summary>
public sealed class SettingsStore(OptimisarrDbContext db)
{
    /// <summary>
    /// The legacy single library root, if one was configured before the
    /// multi-library model existed. Used once by the seeder to migrate it.
    /// </summary>
    public async Task<string?> GetLibraryRootAsync(CancellationToken cancellationToken)
    {
        var setting = await db.AppSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == SettingKeys.LibraryRoot, cancellationToken);

        return setting?.Value;
    }
}
