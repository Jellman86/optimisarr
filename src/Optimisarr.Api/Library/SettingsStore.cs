using Microsoft.EntityFrameworkCore;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

/// <summary>Reads and writes well-known application settings in the database.</summary>
public sealed class SettingsStore(OptimisarrDbContext db)
{
    /// <summary>Conservative default: process one job at a time until the user opts in to more.</summary>
    public const int DefaultMaxConcurrentJobs = 1;

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

    public async Task<int> GetMaxConcurrentJobsAsync(CancellationToken cancellationToken)
    {
        var setting = await db.AppSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == SettingKeys.MaxConcurrentJobs, cancellationToken);

        return int.TryParse(setting?.Value, out var value) && value >= 1
            ? value
            : DefaultMaxConcurrentJobs;
    }

    /// <summary>Sets the global concurrency limit. Clamped to at least 1.</summary>
    public async Task SetMaxConcurrentJobsAsync(int value, CancellationToken cancellationToken)
    {
        var clamped = Math.Max(1, value);
        await UpsertAsync(SettingKeys.MaxConcurrentJobs, clamped.ToString(), cancellationToken);
    }

    private async Task UpsertAsync(string key, string value, CancellationToken cancellationToken)
    {
        var setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (setting is null)
        {
            db.AppSettings.Add(new AppSetting { Key = key, Value = value, UpdatedAt = DateTimeOffset.UtcNow });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
