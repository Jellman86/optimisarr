namespace Optimisarr.Data;

/// <summary>Well-known keys for rows in the <see cref="AppSetting"/> table.</summary>
public static class SettingKeys
{
    /// <summary>Absolute path to the single configured media library root.</summary>
    public const string LibraryRoot = "library.root";

    /// <summary>Maximum number of transcode jobs allowed to run at once across all libraries.</summary>
    public const string MaxConcurrentJobs = "queue.maxConcurrentJobs";
}
