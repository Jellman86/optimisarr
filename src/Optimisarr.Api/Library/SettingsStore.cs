using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Data;
using System.Globalization;

namespace Optimisarr.Api.Library;

public sealed record QueueSettings(
    int MaxConcurrentJobs,
    bool ScheduleEnabled,
    TimeOnly ScheduleWindowStart,
    TimeOnly ScheduleWindowEnd,
    long MinFreeDiskBytes,
    int CpuThreadLimit,
    EncoderMode EncoderMode,
    VerificationPolicy VerificationPolicy);

/// <summary>Reads and writes well-known application settings in the database.</summary>
public sealed class SettingsStore(OptimisarrDbContext db)
{
    /// <summary>Conservative default: process one job at a time until the user opts in to more.</summary>
    public const int DefaultMaxConcurrentJobs = 1;

    public static readonly TimeOnly DefaultScheduleWindowStart = new(0, 0);
    public static readonly TimeOnly DefaultScheduleWindowEnd = new(0, 0);
    public const long DefaultMinFreeDiskBytes = 10L * 1024 * 1024 * 1024;

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

    public async Task<QueueSettings> GetQueueSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await db.AppSettings
            .AsNoTracking()
            .Where(setting => setting.Key == SettingKeys.MaxConcurrentJobs
                || setting.Key == SettingKeys.ScheduleEnabled
                || setting.Key == SettingKeys.ScheduleWindowStart
                || setting.Key == SettingKeys.ScheduleWindowEnd
                || setting.Key == SettingKeys.MinFreeDiskBytes
                || setting.Key == SettingKeys.CpuThreadLimit
                || setting.Key == SettingKeys.EncoderMode
                || setting.Key == SettingKeys.VerificationDurationTolerancePercent
                || setting.Key == SettingKeys.VerificationRequireAudioRetained
                || setting.Key == SettingKeys.VerificationRequireSubtitlesRetained
                || setting.Key == SettingKeys.VerificationRequireSizeReduction)
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, cancellationToken);

        return new QueueSettings(
            ParseInt(settings.GetValueOrDefault(SettingKeys.MaxConcurrentJobs), DefaultMaxConcurrentJobs, min: 1),
            bool.TryParse(settings.GetValueOrDefault(SettingKeys.ScheduleEnabled), out var enabled) && enabled,
            ParseTime(settings.GetValueOrDefault(SettingKeys.ScheduleWindowStart), DefaultScheduleWindowStart),
            ParseTime(settings.GetValueOrDefault(SettingKeys.ScheduleWindowEnd), DefaultScheduleWindowEnd),
            ParseLong(settings.GetValueOrDefault(SettingKeys.MinFreeDiskBytes), DefaultMinFreeDiskBytes, min: 0),
            ParseInt(settings.GetValueOrDefault(SettingKeys.CpuThreadLimit), fallback: 0, min: 0),
            ParseEnum(settings.GetValueOrDefault(SettingKeys.EncoderMode), EncoderMode.Auto),
            new VerificationPolicy(
                ParseDouble(
                    settings.GetValueOrDefault(SettingKeys.VerificationDurationTolerancePercent),
                    VerificationPolicy.Default.DurationTolerancePercent,
                    min: 0),
                ParseBool(
                    settings.GetValueOrDefault(SettingKeys.VerificationRequireAudioRetained),
                    VerificationPolicy.Default.RequireAudioRetained),
                ParseBool(
                    settings.GetValueOrDefault(SettingKeys.VerificationRequireSubtitlesRetained),
                    VerificationPolicy.Default.RequireSubtitlesRetained),
                ParseBool(
                    settings.GetValueOrDefault(SettingKeys.VerificationRequireSizeReduction),
                    VerificationPolicy.Default.RequireSizeReduction)));
    }

    /// <summary>Sets the global concurrency limit. Clamped to at least 1.</summary>
    public async Task SetMaxConcurrentJobsAsync(int value, CancellationToken cancellationToken)
    {
        var clamped = Math.Max(1, value);
        await UpsertAsync(SettingKeys.MaxConcurrentJobs, clamped.ToString(), cancellationToken);
    }

    public async Task SetQueueSettingsAsync(QueueSettings settings, CancellationToken cancellationToken)
    {
        await UpsertManyAsync(new Dictionary<string, string>
        {
            [SettingKeys.MaxConcurrentJobs] = Math.Max(1, settings.MaxConcurrentJobs).ToString(CultureInfo.InvariantCulture),
            [SettingKeys.ScheduleEnabled] = settings.ScheduleEnabled.ToString(CultureInfo.InvariantCulture),
            [SettingKeys.ScheduleWindowStart] = FormatTime(settings.ScheduleWindowStart),
            [SettingKeys.ScheduleWindowEnd] = FormatTime(settings.ScheduleWindowEnd),
            [SettingKeys.MinFreeDiskBytes] = Math.Max(0, settings.MinFreeDiskBytes).ToString(CultureInfo.InvariantCulture),
            [SettingKeys.CpuThreadLimit] = Math.Max(0, settings.CpuThreadLimit).ToString(CultureInfo.InvariantCulture),
            [SettingKeys.EncoderMode] = settings.EncoderMode.ToString(),
            [SettingKeys.VerificationDurationTolerancePercent] =
                Math.Max(0, settings.VerificationPolicy.DurationTolerancePercent).ToString(CultureInfo.InvariantCulture),
            [SettingKeys.VerificationRequireAudioRetained] =
                settings.VerificationPolicy.RequireAudioRetained.ToString(CultureInfo.InvariantCulture),
            [SettingKeys.VerificationRequireSubtitlesRetained] =
                settings.VerificationPolicy.RequireSubtitlesRetained.ToString(CultureInfo.InvariantCulture),
            [SettingKeys.VerificationRequireSizeReduction] =
                settings.VerificationPolicy.RequireSizeReduction.ToString(CultureInfo.InvariantCulture)
        }, cancellationToken);
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

    private async Task UpsertManyAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        var keys = values.Keys.ToArray();
        var existing = await db.AppSettings
            .Where(setting => keys.Contains(setting.Key))
            .ToDictionaryAsync(setting => setting.Key, cancellationToken);

        foreach (var (key, value) in values)
        {
            if (existing.TryGetValue(key, out var setting))
            {
                setting.Value = value;
                setting.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                db.AppSettings.Add(new AppSetting { Key = key, Value = value, UpdatedAt = DateTimeOffset.UtcNow });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static int ParseInt(string? value, int fallback, int min) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= min
            ? parsed
            : fallback;

    private static long ParseLong(string? value, long fallback, long min) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= min
            ? parsed
            : fallback;

    private static double ParseDouble(string? value, double fallback, double min) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed >= min
            ? parsed
            : fallback;

    private static bool ParseBool(string? value, bool fallback) =>
        bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static TimeOnly ParseTime(string? value, TimeOnly fallback) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : fallback;

    private static T ParseEnum<T>(string? value, T fallback)
        where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : fallback;

    private static string FormatTime(TimeOnly value) => value.ToString("HH:mm", CultureInfo.InvariantCulture);
}
