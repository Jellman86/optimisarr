using System.Globalization;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;

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
            if (!TrackLanguages.TryNormaliseLanguageList(library.KeepAudioLanguages, out _))
            {
                errors.Add(
                    $"{where} kept audio languages must be comma-separated ISO 639 codes " +
                    $"of 2–3 letters and at most {TrackLanguages.MaxLanguageListLength} characters.");
            }
            if (!TrackLanguages.TryNormaliseLanguageList(library.KeepSubtitleLanguages, out _))
            {
                errors.Add(
                    $"{where} kept subtitle languages must be comma-separated ISO 639 codes " +
                    $"of 2–3 letters and at most {TrackLanguages.MaxLanguageListLength} characters.");
            }
            if (library.TargetImageFormat is { } targetImageFormat
                && !ImageTarget.IsEncodable(targetImageFormat)
                && !targetImageFormat.Equals("avif", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"{where} image target is not supported: {targetImageFormat}. " +
                    $"Expected one of {string.Join(", ", ImageTarget.EncodableFormats)}.");
            }
            if (RequireEnum<ImageDownscaleMode>(library.ImageDownscaleMode, $"{where} image downscale mode", errors, out var downscaleMode))
            {
                if (downscaleMode == ImageDownscaleMode.MaxLongEdge && library.ImageDownscaleValue is < 16 or > 100_000)
                {
                    errors.Add($"{where} image max long-edge must be between 16 and 100000 pixels.");
                }
                if (downscaleMode == ImageDownscaleMode.Percent && library.ImageDownscaleValue is < 1 or > 99)
                {
                    errors.Add($"{where} image downscale percentage must be between 1 and 99.");
                }
            }
            RequireWindowTime(library.AutoEnqueueWindowStart, $"{where} auto-enqueue window start", errors);
            RequireWindowTime(library.AutoEnqueueWindowEnd, $"{where} auto-enqueue window end", errors);
            RequireRange(library.MinVmafHarmonicMean, 0, 100, $"{where} VMAF harmonic-mean floor", errors);
            RequireRange(library.MinVmafMin, 0, 100, $"{where} VMAF fifth-percentile floor", errors);
            RequireRange(library.MinVmafCatastrophicMin, 0, 100, $"{where} VMAF catastrophic floor", errors);
            RequireRange(library.VmafFrameSubsample, 1, 10, $"{where} VMAF frame sampling interval", errors);
            if (library.MinVmafCatastrophicMin is { } catastrophic
                && library.MinVmafMin is { } fifth
                && catastrophic > fifth)
            {
                errors.Add($"{where} VMAF catastrophic floor cannot exceed the fifth-percentile floor.");
            }
            if (library.MinVmafMin is { } percentile
                && library.MinVmafHarmonicMean is { } harmonic
                && percentile > harmonic)
            {
                errors.Add($"{where} VMAF fifth-percentile floor cannot exceed the harmonic-mean floor.");
            }
            if (library.MinVmafMin is null
                && library.MinVmafCatastrophicMin is { } catastrophicFloor
                && library.MinVmafHarmonicMean is { } harmonicFloor
                && catastrophicFloor > harmonicFloor)
            {
                errors.Add($"{where} VMAF catastrophic floor cannot exceed the harmonic-mean floor.");
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

        for (var i = 0; i < snapshot.ArrConnections.Count; i++)
        {
            var connection = snapshot.ArrConnections[i];
            var where = $"Sonarr/Radarr connection #{i + 1}";
            RequireText(connection.Name, $"{where} name", errors);
            RequireText(connection.BaseUrl, $"{where} base URL", errors);
            RequireEnum<ArrConnectionType>(connection.Type, $"{where} type", errors);
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
        _ = RequireEnum<T>(value, label, errors, out _);
    }

    private static bool RequireEnum<T>(string? value, string label, List<string> errors, out T parsed)
        where T : struct, Enum
    {
        if (!Enum.TryParse<T>(value, ignoreCase: true, out parsed))
        {
            errors.Add($"{label} is not valid: {value}.");
            return false;
        }

        return true;
    }

    private static void RequireWindowTime(string? value, string label, List<string> errors)
    {
        if (!TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            errors.Add($"{label} must use HH:mm format.");
        }
    }

    private static void RequireRange(double? value, double minimum, double maximum, string label, List<string> errors)
    {
        if (value is { } number && (!double.IsFinite(number) || number < minimum || number > maximum))
        {
            errors.Add($"{label} must be between {minimum} and {maximum}.");
        }
    }
}
