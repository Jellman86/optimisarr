namespace Optimisarr.Core.Activity;

/// <summary>
/// What makes a Sonarr/Radarr path mapping acceptable, applied the same way to one saved from Settings
/// and to one restored from a backup. Both folders are full paths that never climb with <c>..</c>; the
/// manager's is judged by its own style because it may run on another operating system.
/// </summary>
public static class ArrPathMappingRules
{
    public const int MaxPrefixLength = 1024;

    public const string Error =
        "A path mapping needs both folders: where the manager sees the library, and where Optimisarr sees it, each a full path.";

    /// <summary>
    /// True for two blank folders (no mapping) or two acceptable ones, returned trimmed. A half-filled
    /// mapping, or one that climbs, is refused rather than guessed at.
    /// </summary>
    public static bool TryParse(string? remotePrefix, string? localPrefix, out ArrPathMapping? mapping)
    {
        mapping = null;
        var remote = string.IsNullOrWhiteSpace(remotePrefix) ? null : remotePrefix.Trim();
        var local = string.IsNullOrWhiteSpace(localPrefix) ? null : localPrefix.Trim();
        if (remote is null && local is null)
        {
            return true;
        }

        if (remote is null || local is null || !IsAbsoluteManagerPath(remote) || !Path.IsPathFullyQualified(local)
            || Climbs(remote) || Climbs(local) || remote.Length > MaxPrefixLength || local.Length > MaxPrefixLength)
        {
            return false;
        }

        mapping = new ArrPathMapping(remote, local);
        return true;
    }

    private static bool IsAbsoluteManagerPath(string path) =>
        path.StartsWith('/') || path.StartsWith(@"\\", StringComparison.Ordinal)
        || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/');

    private static bool Climbs(string path) => path.Split('/', '\\').Contains("..");
}
