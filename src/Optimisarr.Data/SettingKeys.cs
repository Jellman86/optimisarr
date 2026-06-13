namespace Optimisarr.Data;

/// <summary>Well-known keys for rows in the <see cref="AppSetting"/> table.</summary>
public static class SettingKeys
{
    /// <summary>Absolute path to the single configured media library root.</summary>
    public const string LibraryRoot = "library.root";

    /// <summary>Maximum number of transcode jobs allowed to run at once across all libraries.</summary>
    public const string MaxConcurrentJobs = "queue.maxConcurrentJobs";

    /// <summary>Whether new transcode jobs may start only inside a processing window.</summary>
    public const string ScheduleEnabled = "queue.schedule.enabled";

    /// <summary>Local time at which the processing window starts, formatted HH:mm.</summary>
    public const string ScheduleWindowStart = "queue.schedule.windowStart";

    /// <summary>Local time at which the processing window ends, formatted HH:mm.</summary>
    public const string ScheduleWindowEnd = "queue.schedule.windowEnd";

    /// <summary>Minimum free bytes on the work filesystem before new jobs may start.</summary>
    public const string MinFreeDiskBytes = "queue.minFreeDiskBytes";

    /// <summary>Maximum CPU threads ffmpeg may use per job; 0 lets ffmpeg decide.</summary>
    public const string CpuThreadLimit = "queue.cpuThreadLimit";

    /// <summary>Preferred encoder mode for new transcode jobs.</summary>
    public const string EncoderMode = "queue.encoderMode";

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

    /// <summary>Whether replacement may fall back to copy-plus-delete across filesystems.</summary>
    public const string ReplacementAllowCrossFilesystem = "replacement.allowCrossFilesystem";

    /// <summary>How many days quarantined originals should be retained; 0 means indefinitely.</summary>
    public const string ReplacementQuarantineRetentionDays = "replacement.quarantineRetentionDays";

    /// <summary>Stable client identifier Optimisarr presents to Plex during the OAuth/PIN flow.</summary>
    public const string PlexClientIdentifier = "connect.plexClientIdentifier";
}
