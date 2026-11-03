using Optimisarr.Core.Queue;

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
        if (!Directory.Exists(root))
        {
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
}
