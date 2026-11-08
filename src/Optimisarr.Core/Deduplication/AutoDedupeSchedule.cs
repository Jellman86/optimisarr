using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Deduplication;

/// <summary>What the last automatic duplicate cleanup of a library did.</summary>
public enum AutoDedupeOutcome
{
    /// <summary>Spare copies were moved into quarantine.</summary>
    Cleaned,

    /// <summary>The scan finished and no copy could safely move.</summary>
    NothingToClean,

    /// <summary>Dry-run mode is on, so nothing was scanned or moved.</summary>
    DryRun,

    /// <summary>The library has more candidates than one scan reads.</summary>
    TooManyFiles,

    /// <summary>The scan stopped, failed, or left no report to clean from.</summary>
    ScanFailed,

    /// <summary>Some copies moved and others could not.</summary>
    PartlyCleaned,

    /// <summary>No copy that was meant to move could be moved.</summary>
    CleanupFailed,

    /// <summary>Sonarr or Radarr could not say what it tracks, so every copy stayed.</summary>
    ManagerUnavailable,
}

/// <summary>
/// When a library's automatic duplicate cleanup runs, and over which libraries. It runs at most once a
/// day, because a scan reads every new or changed file in full.
/// </summary>
public static class AutoDedupeSchedule
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>Cleanup only moves copies in film and TV libraries, so only those can be scheduled.</summary>
    public static bool Supports(MediaType mediaType) => mediaType is MediaType.Film or MediaType.Tv;

    /// <summary>A clock that went backwards counts as due, so a wrong date never stops the schedule.</summary>
    public static bool IsDue(DateTimeOffset? lastRunUtc, DateTimeOffset nowUtc) =>
        lastRunUtc is not { } last || last > nowUtc || nowUtc - last >= Interval;

    /// <summary>
    /// The libraries to scan: the same ones as the last scan, so the keeper choices saved for that report
    /// still apply. When one of them has gone, or the last scan did not include this library, only this
    /// library is scanned.
    /// </summary>
    public static IReadOnlyList<int> Scope(int libraryId, IReadOnlyCollection<int> lastScope, IReadOnlySet<int> existingLibraryIds)
    {
        if (!lastScope.Contains(libraryId) || !lastScope.All(existingLibraryIds.Contains)) return [libraryId];
        return [.. lastScope.Distinct().Order()];
    }
}
