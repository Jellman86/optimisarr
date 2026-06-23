using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Data;

namespace Optimisarr.Api.Stats;

/// <summary>Persistent lifetime savings, accrued across every replacement that has ever been in place.</summary>
public sealed record LifetimeStats(long FilesOptimised, long OriginalBytes, long OptimisedBytes)
{
    public static readonly LifetimeStats Empty = new(0, 0, 0);

    public long BytesSaved => Math.Max(0, OriginalBytes - OptimisedBytes);

    public double AverageSavingPercent => StatsQueries.SavingPercent(OriginalBytes, OptimisedBytes);
}

/// <summary>
/// Maintains the Dashboard's headline "total space saved" as a durable running tally in
/// <see cref="AppSetting"/> rows, rather than deriving it from current <see cref="Replacement"/>
/// rows. Deriving was fragile: purging quarantine, clearing queue/replacement history, or removing
/// a library could all erase rows and silently shrink the figure, and a wiped row set reads as zero.
/// The tally instead accrues when a replacement is put in place and is reduced when one is rolled
/// back, so it reflects realised lifetime savings and only the operator's explicit reset clears it.
/// </summary>
public sealed class LifetimeStatsStore(OptimisarrDbContext db)
{
    public async Task<LifetimeStats> GetAsync(CancellationToken cancellationToken)
    {
        var values = await db.AppSettings
            .AsNoTracking()
            .Where(setting => setting.Key == SettingKeys.LifetimeFilesOptimised
                || setting.Key == SettingKeys.LifetimeOriginalBytes
                || setting.Key == SettingKeys.LifetimeOptimisedBytes)
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, cancellationToken);

        return new LifetimeStats(
            Read(values, SettingKeys.LifetimeFilesOptimised),
            Read(values, SettingKeys.LifetimeOriginalBytes),
            Read(values, SettingKeys.LifetimeOptimisedBytes));
    }

    /// <summary>
    /// Adds one in-place replacement to the tally. The change is tracked on the shared context but
    /// not saved, so it commits in the same transaction as the replacement that triggered it.
    /// </summary>
    public Task ApplyReplacementAsync(long originalBytes, long newBytes, CancellationToken cancellationToken) =>
        AdjustAsync(filesDelta: 1, originalDelta: originalBytes, optimisedDelta: newBytes, cancellationToken);

    /// <summary>Reverses a previously counted replacement after a rollback restored its original.</summary>
    public Task ApplyRollbackAsync(long originalBytes, long newBytes, CancellationToken cancellationToken) =>
        AdjustAsync(filesDelta: -1, originalDelta: -originalBytes, optimisedDelta: -newBytes, cancellationToken);

    /// <summary>Resets the lifetime tally to zero (the Dashboard "Reset" action) and saves it.</summary>
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await SetAsync(SettingKeys.LifetimeFilesOptimised, 0, cancellationToken);
        await SetAsync(SettingKeys.LifetimeOriginalBytes, 0, cancellationToken);
        await SetAsync(SettingKeys.LifetimeOptimisedBytes, 0, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task AdjustAsync(long filesDelta, long originalDelta, long optimisedDelta, CancellationToken cancellationToken)
    {
        await BumpAsync(SettingKeys.LifetimeFilesOptimised, filesDelta, cancellationToken);
        await BumpAsync(SettingKeys.LifetimeOriginalBytes, originalDelta, cancellationToken);
        await BumpAsync(SettingKeys.LifetimeOptimisedBytes, optimisedDelta, cancellationToken);
    }

    private async Task BumpAsync(string key, long delta, CancellationToken cancellationToken)
    {
        // FindAsync resolves an already-tracked (including pending-unsaved) row before hitting the
        // database, so two adjustments in one transaction compound rather than clash on the key.
        var row = await db.AppSettings.FindAsync([key], cancellationToken);
        var current = row is not null && long.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
        // Clamp at zero: a rollback can never drive a counter below zero (e.g. if the tally was
        // reset while a replacement was still live), keeping the figure honest.
        Write(ref row, key, Math.Max(0, current + delta));
    }

    private async Task SetAsync(string key, long value, CancellationToken cancellationToken)
    {
        var row = await db.AppSettings.FindAsync([key], cancellationToken);
        Write(ref row, key, value);
    }

    private void Write(ref AppSetting? row, string key, long value)
    {
        if (row is null)
        {
            row = new AppSetting { Key = key };
            db.AppSettings.Add(row);
        }

        row.Value = value.ToString(CultureInfo.InvariantCulture);
        row.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static long Read(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var raw)
            && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
}
