namespace Optimisarr.Core.Activity;

/// <summary>
/// Rewrites the start of a path as the manager sees it into the same place as Optimisarr sees it,
/// for when the two mount the library differently. Both prefixes are whole folders.
/// </summary>
public sealed record ArrPathMapping(string RemotePrefix, string LocalPrefix);

/// <summary>A library a webhook file may land in: its id and the root Optimisarr scans.</summary>
public sealed record WebhookLibraryRoot(int Id, string Root);

/// <summary>Where a file a manager reported belongs.</summary>
public abstract record WebhookPathResolution
{
    private WebhookPathResolution() { }

    public sealed record InLibrary(int LibraryId, string LocalPath) : WebhookPathResolution;

    public sealed record NotInLibrary(string LocalPath) : WebhookPathResolution;

    public sealed record Refused(string Reason) : WebhookPathResolution;
}

/// <summary>
/// Turns a path from a Sonarr/Radarr webhook into a file in one of Optimisarr's libraries. The path
/// comes from outside, so anything that could climb out of a folder is refused before it is compared,
/// and a library is chosen only when the file sits strictly beneath its root, by whole folders. Pure:
/// this never touches the filesystem, so the caller still checks the file itself for links.
/// </summary>
public static class ArrWebhookPathResolver
{
    public const string UnsafePathReason = "The manager sent a path that is not a plain absolute file path.";

    public static WebhookPathResolution Resolve(
        string arrPath, ArrPathMapping? mapping, IReadOnlyList<WebhookLibraryRoot> libraries)
    {
        if (string.IsNullOrWhiteSpace(arrPath) || arrPath.Contains('\0') || HasParentSegment(arrPath))
        {
            return new WebhookPathResolution.Refused(UnsafePathReason);
        }

        var local = mapping is null ? arrPath : Map(arrPath, mapping) ?? arrPath;
        if (!Path.IsPathFullyQualified(local) || HasParentSegment(local))
        {
            return new WebhookPathResolution.Refused(UnsafePathReason);
        }

        local = Path.GetFullPath(local);
        var best = libraries
            .Select(library => (library.Id, Root: NormaliseRoot(library.Root)))
            .Where(library => library.Root.Length > 0 && IsStrictlyBeneath(local, library.Root))
            .OrderByDescending(library => library.Root.Length)
            .FirstOrDefault();

        return best.Root is null
            ? new WebhookPathResolution.NotInLibrary(local)
            : new WebhookPathResolution.InLibrary(best.Id, local);
    }

    /// <summary>
    /// Joins a title folder and a file's path relative to it the way the manager would, so a Windows
    /// manager's folder keeps its backslashes until a mapping translates it.
    /// </summary>
    public static string Join(string folder, string relativePath)
    {
        var separator = LooksLikeWindowsPath(folder) ? '\\' : '/';
        return folder.TrimEnd('/', '\\') + separator + relativePath.TrimStart('/', '\\');
    }

    private static string? Map(string arrPath, ArrPathMapping mapping)
    {
        var windows = LooksLikeWindowsPath(mapping.RemotePrefix) || LooksLikeWindowsPath(arrPath);
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var path = windows ? arrPath.Replace('\\', '/') : arrPath;
        var prefix = (windows ? mapping.RemotePrefix.Replace('\\', '/') : mapping.RemotePrefix).TrimEnd('/');
        if (prefix.Length == 0)
        {
            return null;
        }

        if (!path.StartsWith(prefix, comparison) || (path.Length > prefix.Length && path[prefix.Length] != '/'))
        {
            return null;
        }

        var remainder = path[prefix.Length..].TrimStart('/');
        var localRoot = mapping.LocalPrefix.TrimEnd('/', '\\');
        return remainder.Length == 0 ? localRoot : localRoot + "/" + remainder;
    }

    // A drive letter or a UNC share: the manager runs on Windows, where both separators mean a folder.
    private static bool LooksLikeWindowsPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal)
        || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/');

    private static bool HasParentSegment(string path) =>
        path.Split('/', '\\').Any(segment => segment == "..");

    private static string NormaliseRoot(string root) =>
        Path.IsPathFullyQualified(root) && !HasParentSegment(root)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
            : string.Empty;

    private static bool IsStrictlyBeneath(string path, string root) =>
        Path.EndsInDirectorySeparator(root)
            ? path.Length > root.Length && path.StartsWith(root, StringComparison.Ordinal)
            : path.Length > root.Length + 1
        && path.StartsWith(root, StringComparison.Ordinal)
        && (path[root.Length] == Path.DirectorySeparatorChar || path[root.Length] == Path.AltDirectorySeparatorChar);
}
