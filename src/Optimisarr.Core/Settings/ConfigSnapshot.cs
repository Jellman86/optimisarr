namespace Optimisarr.Core.Settings;

/// <summary>
/// A portable, secret-free representation of Optimisarr's configuration: app
/// settings plus library, activity-watcher, and notification-target definitions.
/// Provider tokens are deliberately excluded so an exported file is safe to store
/// and share; on import the user re-enters them. Ids and timestamps are excluded
/// too — a snapshot describes configuration, not a specific database's rows.
/// </summary>
public sealed record ConfigSnapshot(
    int Version,
    DateTimeOffset ExportedAt,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyList<LibrarySnapshot> Libraries,
    IReadOnlyList<ActivityWatcherSnapshot> ActivityWatchers,
    IReadOnlyList<NotificationTargetSnapshot> NotificationTargets,
    IReadOnlyList<ArrConnectionSnapshot> ArrConnections)
{
    /// <summary>The current snapshot schema version. Bump when the shape changes incompatibly.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>A library definition, matched on its unique <see cref="Path"/> when imported.</summary>
public sealed record LibrarySnapshot(
    string Name,
    string Path,
    string MediaType,
    string RuleProfile,
    bool Enabled,
    int Priority,
    long? MinFileSizeBytes,
    int? MaxHeight,
    string? TargetVideoCodec,
    string? TargetContainer,
    string? HdrHandling,
    string? ExcludePaths,
    int? QualityCrf,
    string? EncoderPreset,
    bool MoveOnComplete,
    string? TargetFolder);

/// <summary>An activity watcher definition, matched on its <see cref="Name"/> when imported. No token.</summary>
public sealed record ActivityWatcherSnapshot(
    string Name,
    string Type,
    string BaseUrl,
    bool Enabled,
    bool RefreshOnReplace);

/// <summary>A notification target definition, matched on its <see cref="Name"/> when imported. No token.</summary>
public sealed record NotificationTargetSnapshot(
    string Name,
    string Type,
    string Url,
    bool Enabled,
    bool NotifyOnReplacement,
    bool NotifyOnFailure);

/// <summary>A Sonarr/Radarr connection definition, matched on its <see cref="Name"/> when imported. No API key.</summary>
public sealed record ArrConnectionSnapshot(
    string Name,
    string Type,
    string BaseUrl,
    bool Enabled);
