using Optimisarr.Core.Queue;
using Optimisarr.Core.Library;

namespace Optimisarr.Api.Queue;

/// <summary>
/// Restart-safe cleanup for super-resolution scratch. No attempt can be running before the queue
/// starts, so everything under the dedicated root is left over from a crash or a kill and is
/// removed; nothing outside that root is touched.
/// </summary>
internal static class SuperResolutionScratch
{
    public static void ClearAtStartup(string workRoot)
    {
        var root = WorkOutputRoot.SuperResolutionRoot(workRoot);
        if (!LibraryPathSafety.IsSafeDirectory(Path.GetFullPath(root)))
        {
            // A linked root points outside the work folder; never clear through it. Attempts
            // refuse to run under it too (see the pipeline's workspace check).
            return;
        }
        foreach (var attempt in Directory.EnumerateDirectories(root))
        {
            if (new DirectoryInfo(attempt).LinkTarget is not null)
            {
                // Never follow a link out of the scratch root.
                Directory.Delete(attempt);
                continue;
            }
            Directory.Delete(attempt, recursive: true);
        }
    }

    internal static string? PrepareAttempt(string workspace)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(workspace))!;
        if (new DirectoryInfo(root).LinkTarget is not null || new DirectoryInfo(workspace).LinkTarget is not null)
            return "Super-resolution scratch is linked outside its working directory; no tools were started.";
        if (Directory.Exists(workspace) || File.Exists(workspace))
            return "Super-resolution requires a fresh attempt directory; existing evidence was retained.";

        Directory.CreateDirectory(root);
        if (!LibraryPathSafety.IsSafeDirectory(root))
            return "Super-resolution scratch is linked or cannot be checked safely; no tools were started.";
        Directory.CreateDirectory(workspace);
        return LibraryPathSafety.IsSafeDirectory(Path.GetFullPath(workspace)) ? null
            : "Super-resolution attempt is linked or cannot be checked safely; no tools were started.";
    }

    internal static bool MayRemoveAttempt(string workspace) =>
        LibraryPathSafety.IsSafeDirectory(Path.GetDirectoryName(Path.GetFullPath(workspace))!)
        && LibraryPathSafety.IsSafeDirectory(Path.GetFullPath(workspace));
}
