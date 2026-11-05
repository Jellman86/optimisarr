namespace Optimisarr.Core.Deduplication;

/// <summary>
/// Whether a file stands alone. Disc structures are many files that only work together, so
/// removing one "exact copy" from inside one would break the rest. They are refused, not adapted.
/// </summary>
public static class DedupeFileEligibility
{
    private static readonly HashSet<string> DiscFolders = new(StringComparer.OrdinalIgnoreCase)
        { "VIDEO_TS", "AUDIO_TS", "BDMV", "CERTIFICATE", "HVDVD_TS" };

    private static readonly HashSet<string> DiscExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ifo", ".bup", ".vob", ".bdmv", ".clpi", ".mpls", ".evo" };

    public static bool IsIndependentFile(string pathWithinRoot)
    {
        var parts = pathWithinRoot.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0
            && !parts.Any(DiscFolders.Contains)
            && !DiscExtensions.Contains(Path.GetExtension(parts[^1]));
    }
}
