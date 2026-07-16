namespace Optimisarr.Api.Queue;

/// <summary>
/// Shared helpers for the <c>/work</c> scratch area: resolving its root, and cleaning up the
/// empty per-media-file directories a finished job leaves behind. A job's output lives under
/// <c>/work/&lt;mediaFileId&gt;/…</c> (see <see cref="Optimisarr.Core.Queue.WorkOutputRoot"/>), so
/// once the output is deleted or moved out, that scratch tree should not accumulate forever.
/// </summary>
public static class WorkPaths
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static string Resolve(IHostEnvironment environment)
    {
        var configured = Environment.GetEnvironmentVariable("OPTIMISARR_WORK_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return Directory.Exists("/work")
            ? "/work"
            : Path.Combine(environment.ContentRootPath, "work");
    }

    /// <summary>
    /// Deletes the now-empty directories left under <paramref name="workRoot"/> after the file at
    /// <paramref name="filePath"/> has been removed or moved away. Walks up from the file's
    /// directory and deletes each directory while it is empty, stopping at the first non-empty one
    /// and never deleting (or walking above) the work root itself. Best-effort and safe: a
    /// non-empty directory is never removed, so this can only ever delete empty scratch folders.
    /// </summary>
    public static void PruneEmptyAncestors(string workRoot, string filePath)
    {
        string root, dir;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workRoot));
            dir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return;
        }

        while (!string.IsNullOrEmpty(dir)
            && !PathsEqual(dir, root)
            && IsUnder(dir, root)
            && Directory.Exists(dir)
            && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            try
            {
                Directory.Delete(dir);
            }
            catch (IOException)
            {
                break;
            }
            catch (UnauthorizedAccessException)
            {
                break;
            }

            dir = Path.GetDirectoryName(dir) ?? string.Empty;
        }
    }

    /// <summary>
    /// Returns the free bytes on the mounted filesystem that actually contains
    /// <paramref name="path"/>. On Linux a bind mount such as <c>/work</c> can live on a
    /// different filesystem from <c>/</c>, so <see cref="Path.GetPathRoot(string)"/> is not
    /// sufficient.
    /// </summary>
    public static long? TryGetAvailableFreeSpace(string path)
    {
        try
        {
            var target = NearestExistingDirectory(path);
            if (target is null)
            {
                return null;
            }

            var drives = DriveInfo.GetDrives().Where(drive => drive.IsReady).ToList();
            var mount = SelectContainingMount(target, drives.Select(drive => drive.Name));
            return mount is null
                ? null
                : drives.First(drive => PathsEqual(drive.Name, mount)).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Returns true only when <paramref name="path"/> is strictly below the work root.</summary>
    public static bool IsUnderRoot(string workRoot, string path)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workRoot));
            var candidate = Path.GetFullPath(path);
            return IsUnder(candidate, root);
        }
        catch (Exception exception) when (exception is ArgumentException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Finds old numeric per-media scratch directories that no persisted job still references.
    /// The caller owns deletion so failures can be logged without hiding them in this pure selector.
    /// </summary>
    internal static IReadOnlyList<string> FindStaleOrphanDirectories(
        string workRoot,
        ISet<int> referencedMediaFileIds,
        DateTime cutoffUtc)
    {
        if (!Directory.Exists(workRoot))
        {
            return [];
        }

        return Directory.EnumerateDirectories(workRoot)
            .Where(directory => int.TryParse(Path.GetFileName(directory), out var mediaFileId)
                && !referencedMediaFileIds.Contains(mediaFileId)
                && Directory.GetLastWriteTimeUtc(directory) <= cutoffUtc)
            .ToList();
    }

    internal static string? SelectContainingMount(string path, IEnumerable<string> mountPoints)
    {
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return mountPoints
            .Select(mount => Path.TrimEndingDirectorySeparator(Path.GetFullPath(mount)))
            .Where(mount => PathsEqual(target, mount) || IsUnder(target, mount))
            .OrderByDescending(mount => mount.Length)
            .FirstOrDefault();
    }

    private static string? NearestExistingDirectory(string path)
    {
        var target = Path.GetFullPath(path);
        if (File.Exists(target))
        {
            target = Path.GetDirectoryName(target) ?? target;
        }

        while (!Directory.Exists(target))
        {
            var parent = Path.GetDirectoryName(target);
            if (string.IsNullOrWhiteSpace(parent) || PathsEqual(parent, target))
            {
                return null;
            }
            target = parent;
        }

        return target;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(a),
            Path.TrimEndingDirectorySeparator(b),
            PathComparison);

    // The candidate directory must sit strictly inside the work root for pruning to be considered.
    private static bool IsUnder(string candidate, string root)
    {
        var trimmedRoot = Path.TrimEndingDirectorySeparator(root);
        if (PathsEqual(candidate, trimmedRoot))
        {
            return false;
        }

        var prefix = Path.EndsInDirectorySeparator(trimmedRoot)
            ? trimmedRoot
            : trimmedRoot + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, PathComparison);
    }
}
