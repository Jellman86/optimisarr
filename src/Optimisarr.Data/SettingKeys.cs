namespace Optimisarr.Data;

/// <summary>Well-known keys for rows in the <see cref="AppSetting"/> table.</summary>
public static class SettingKeys
{
    /// <summary>Absolute path to the single configured media library root.</summary>
    public const string LibraryRoot = "library.root";

    /// <summary>Maximum number of transcode jobs allowed to run at once across all libraries.</summary>
    public const string MaxConcurrentJobs = "queue.maxConcurrentJobs";

    /// <summary>Minimum free bytes on the work filesystem before new jobs may start.</summary>
    public const string MinFreeDiskBytes = "queue.minFreeDiskBytes";

    /// <summary>Maximum CPU threads ffmpeg may use per job; 0 lets ffmpeg decide.</summary>
    public const string CpuThreadLimit = "queue.cpuThreadLimit";

    /// <summary>How often (hours) every enabled library is rescanned for new/changed files.</summary>
    public const string LibraryScanIntervalHours = "library.scanIntervalHours";

    /// <summary>Preferred encoder mode for new transcode jobs.</summary>
    public const string EncoderMode = "queue.encoderMode";

    /// <summary>
    /// Whether to hardware-decode the source when a hardware encoder is in use, falling back
    /// to software decode automatically if the GPU cannot decode a given source.
    /// </summary>
    public const string HardwareDecode = "queue.hardwareDecode";

    /// <summary>
    /// One-shot marker: the media-kind backfill has reset legacy Unknown-kind files for re-probe.
    /// Present (any value) means it has run, so it never runs again.
    /// </summary>
    public const string MediaKindBackfillDone = "maintenance.mediaKindBackfillV1Done";

    /// <summary>Allowed output duration drift, as a percentage of original duration.</summary>
    public const string VerificationDurationTolerancePercent = "verification.durationTolerancePercent";

    /// <summary>Whether verification requires all original audio tracks to be retained.</summary>
    public const string VerificationRequireAudioRetained = "verification.requireAudioRetained";

    /// <summary>Whether verification requires all original subtitle tracks to be retained.</summary>
    public const string VerificationRequireSubtitlesRetained = "verification.requireSubtitlesRetained";

    /// <summary>Whether verification requires the output to be smaller than the original.</summary>
    public const string VerificationRequireSizeReduction = "verification.requireSizeReduction";

    /// <summary>Whether the opt-in perceptual-quality (VMAF) gate is enforced.</summary>
    public const string VerificationQualityGateEnabled = "verification.qualityGateEnabled";

    /// <summary>Minimum harmonic-mean VMAF an output must reach when the quality gate is on.</summary>
    public const string VerificationMinimumVmafHarmonicMean = "verification.minimumVmafHarmonicMean";

    /// <summary>Minimum single-frame VMAF an output must reach when the quality gate is on.</summary>
    public const string VerificationMinimumVmafMin = "verification.minimumVmafMin";

    /// <summary>Whether the opt-in EBU R128 audio-loudness drift gate is enforced.</summary>
    public const string VerificationAudioLoudnessGateEnabled = "verification.audioLoudnessGateEnabled";

    /// <summary>Maximum allowed integrated-loudness drift in LU when the loudness gate is on.</summary>
    public const string VerificationMaxLoudnessDriftLufs = "verification.maxLoudnessDriftLufs";

    /// <summary>Whether the opt-in true-peak clipping gate is enforced.</summary>
    public const string VerificationAudioClippingGateEnabled = "verification.audioClippingGateEnabled";

    /// <summary>True-peak ceiling in dBTP above which the output is treated as clipping.</summary>
    public const string VerificationMaxTruePeakDbtp = "verification.maxTruePeakDbtp";

    /// <summary>Whether the opt-in image structural-quality (SSIM) gate is enabled.</summary>
    public const string VerificationImageQualityGateEnabled = "verification.imageQualityGateEnabled";

    /// <summary>Minimum all-channel SSIM (0–1) a re-encoded still must reach to pass.</summary>
    public const string VerificationMinimumImageSsim = "verification.minimumImageSsim";

    /// <summary>Whether the opt-in image EXIF/ICC-retention gate is enabled.</summary>
    public const string VerificationImageMetadataGateEnabled = "verification.imageMetadataGateEnabled";

    /// <summary>Whether replacement may fall back to copy-plus-delete across filesystems.</summary>
    public const string ReplacementAllowCrossFilesystem = "replacement.allowCrossFilesystem";

    /// <summary>Whether replacement and quarantine purge actions are blocked while optimisation can still be tested.</summary>
    public const string DryRunMode = "replacement.dryRunMode";

    /// <summary>How many days quarantined originals should be retained; 0 means indefinitely.</summary>
    public const string ReplacementQuarantineRetentionDays = "replacement.quarantineRetentionDays";

    /// <summary>Stable client identifier Optimisarr presents to Plex during the OAuth/PIN flow.</summary>
    public const string PlexClientIdentifier = "connect.plexClientIdentifier";

    /// <summary>
    /// Lifetime count of files whose optimised version was put in place. This is a persistent
    /// running tally — it accrues on every successful replacement and survives quarantine purge,
    /// queue/history clearing, and restarts, unlike a figure derived from current rows. A rollback
    /// decrements it; the operator can reset it from the Dashboard.
    /// </summary>
    public const string LifetimeFilesOptimised = "stats.lifetimeFilesOptimised";

    /// <summary>Lifetime sum of original sizes for files whose optimised version is/was in place.</summary>
    public const string LifetimeOriginalBytes = "stats.lifetimeOriginalBytes";

    /// <summary>Lifetime sum of optimised sizes for files whose optimised version is/was in place.</summary>
    public const string LifetimeOptimisedBytes = "stats.lifetimeOptimisedBytes";
}
