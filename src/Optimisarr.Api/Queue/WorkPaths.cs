namespace Optimisarr.Api.Queue;

/// <summary>
/// Shared helpers for the <c>/work</c> scratch area: resolving its root, and cleaning up the
/// empty per-media-file directories a finished job leaves behind. A job's output lives under
/// <c>/work/&lt;mediaFileId&gt;/…</c> (see <see cref="Optimisarr.Core.Queue.WorkOutputRoot"/>), so
/// once the output is deleted or moved out, that scratch tree should not accumulate forever.
/// </summary>
public static class WorkPaths
{
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

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(a),
            Path.TrimEndingDirectorySeparator(b),
            PathComparison);

    // The candidate directory must sit strictly inside the work root for pruning to be considered.
    private static bool IsUnder(string candidate, string root)
    {
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, PathComparison);
    }

    // Linux paths are case-sensitive; Windows/macOS are not. Match the host's filesystem.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}
