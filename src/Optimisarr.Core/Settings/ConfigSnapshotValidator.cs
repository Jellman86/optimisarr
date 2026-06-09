using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Settings;

/// <summary>The outcome of validating a config snapshot: valid, or a list of reasons it was rejected.</summary>
public sealed record ConfigValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static readonly ConfigValidationResult Valid = new(true, []);
}

/// <summary>
/// Pure validation for an imported <see cref="ConfigSnapshot"/>. It checks the
/// schema version, that every setting key is one this build recognises, and that
/// every enum-valued and required field is well formed — so a malformed, tampered,
/// or newer-than-supported file is rejected in full before any of it is written.
/// </summary>
public static class ConfigSnapshotValidator
{
    public static ConfigValidationResult Validate(
        ConfigSnapshot snapshot,
        IReadOnlySet<string> allowedSettingKeys)
    {
        var errors = new List<string>();

        if (snapshot.Version < 1)
        {
            errors.Add($"Unsupported config version {snapshot.Version}.");
        }
        else if (snapshot.Version > ConfigSnapshot.CurrentVersion)
        {
            errors.Add(
                $"Config version {snapshot.Version} was exported by a newer Optimisarr " +
                $"(this build supports up to version {ConfigSnapshot.CurrentVersion}).");
        }

        foreach (var key in snapshot.Settings.Keys)
        {
            if (!allowedSettingKeys.Contains(key))
            {
                errors.Add($"Unknown setting key: {key}.");
            }
        }

        for (var i = 0; i < snapshot.Libraries.Count; i++)
        {
            var library = snapshot.Libraries[i];
            var where = $"Library #{i + 1}";
            RequireText(library.Name, $"{where} name", errors);
            RequireText(library.Path, $"{where} path", errors);
            RequireEnum<MediaType>(library.MediaType, $"{where} media type", errors);
            RequireEnum<RuleProfile>(library.RuleProfile, $"{where} rule profile", errors);
            if (library.HdrHandling is not null)
            {
                RequireEnum<HdrHandling>(library.HdrHandling, $"{where} HDR handling", errors);
            }
        }

        for (var i = 0; i < snapshot.ActivityWatchers.Count; i++)
        {
            var watcher = snapshot.ActivityWatchers[i];
            var where = $"Activity watcher #{i + 1}";
            RequireText(watcher.Name, $"{where} name", errors);
            RequireText(watcher.BaseUrl, $"{where} base URL", errors);
            RequireEnum<ActivityWatcherType>(watcher.Type, $"{where} type", errors);
        }

        for (var i = 0; i < snapshot.NotificationTargets.Count; i++)
        {
            var target = snapshot.NotificationTargets[i];
            var where = $"Notification target #{i + 1}";
            RequireText(target.Name, $"{where} name", errors);
            RequireText(target.Url, $"{where} URL", errors);
            RequireEnum<NotificationType>(target.Type, $"{where} type", errors);
        }

        return errors.Count == 0 ? ConfigValidationResult.Valid : new ConfigValidationResult(false, errors);
    }

    private static void RequireText(string? value, string label, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{label} is required.");
        }
    }

    private static void RequireEnum<T>(string? value, string label, List<string> errors)
        where T : struct, Enum
    {
        if (!Enum.TryParse<T>(value, ignoreCase: true, out _))
        {
            errors.Add($"{label} is not valid: {value}.");
        }
    }
}
