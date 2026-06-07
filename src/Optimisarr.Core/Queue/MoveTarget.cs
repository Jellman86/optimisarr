namespace Optimisarr.Core.Queue;

/// <summary>
/// Computes where a completed work output should be moved when a library opts to
/// collect outputs in a target folder. The output keeps the same path relative to
/// the work root, so the target mirrors the library's structure. Pure and tested.
/// </summary>
public static class MoveTarget
{
    public static string Resolve(string workRoot, string workOutputPath, string targetFolder)
    {
        var relative = Path.GetRelativePath(workRoot, workOutputPath).Replace('\\', '/');
        return $"{targetFolder.TrimEnd('/', '\\')}/{relative}";
    }
}
