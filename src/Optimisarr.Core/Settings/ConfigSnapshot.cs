namespace Optimisarr.Core.Settings;

/// <summary>
/// A portable representation of Optimisarr's configuration. It includes provider
/// secrets so a backup can restore a working configuration; it must therefore be
/// treated as sensitive material and never committed or shared. Ids and timestamps
/// are excluded — a snapshot describes configuration, not a specific database.
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
    string? TargetFolder,
    double? MinVmafHarmonicMean = null,
    double? MinVmafMin = null,
    bool AutoEnqueueEnabled = false,
    string AutoEnqueueWindowStart = "00:00",
    string AutoEnqueueWindowEnd = "00:00",
    string? AudioTargetCodec = null,
    int? AudioBitrateKbps = null,
    string? VideoAudioCodec = null,
    int? VideoAudioBitrateKbps = null,
    bool DownmixToStereo = false,
    bool ReencodeLossyAudio = false,
    string? TargetImageFormat = null,
    int? ImageQuality = null,
    bool ReencodeLossyImages = false,
    string ImageDownscaleMode = "None",
    int ImageDownscaleValue = 0,
    bool MoveOverwrite = false,
    bool AutoReplace = false,
    bool? SkipEfficientSources = null,
    bool? OptimiseDolbyVision = null,
    string? KeepAudioLanguages = null,
    bool? VmafQualityGateEnabled = null,
    double? MinVmafCatastrophicMin = null,
    bool? ClipVmafEnabled = null,
    int? VmafFrameSubsample = null,
    string? KeepSubtitleLanguages = null,
    string VideoQualityStrategy = "Fixed",
    double? DurationTolerancePercent = null,
    bool? RequireAudioRetained = null,
    bool? RequireSubtitlesRetained = null,
    bool? RequireSizeReduction = null,
    bool? AudioLoudnessGateEnabled = null,
    double? MaxLoudnessDriftLufs = null,
    bool? AudioClippingGateEnabled = null,
    double? MaxTruePeakDbtp = null,
    bool? ImageQualityGateEnabled = null,
    double? MinimumImageSsim = null,
    bool? ImageMetadataGateEnabled = null,
    // Appended, and nullable, so a snapshot taken before this existed still imports and leaves
    // the exclusion off — which is also its default for a new library.
    bool? ExcludeHardLinkedFiles = null,
    string? SkipSourceCodecs = null,
    string? ContentTune = null,
    int? MaxBitrateKbps = null,
    bool? StrongerAdaptiveQuantisation = null);

/// <summary>An activity watcher definition, matched on its <see cref="Name"/> when imported.</summary>
public sealed record ActivityWatcherSnapshot(
    string Name,
    string Type,
    string BaseUrl,
    bool Enabled,
    bool RefreshOnReplace,
    string? ApiToken = null);

/// <summary>A notification target definition, matched on its <see cref="Name"/> when imported.</summary>
public sealed record NotificationTargetSnapshot(
    string Name,
    string Type,
    string Url,
    bool Enabled,
    bool NotifyOnReplacement,
    bool NotifyOnFailure,
    string? Token = null);

/// <summary>A Sonarr/Radarr connection definition, matched on its <see cref="Name"/> when imported.</summary>
public sealed record ArrConnectionSnapshot(
    string Name,
    string Type,
    string BaseUrl,
    bool Enabled,
    string? ApiKey = null);
