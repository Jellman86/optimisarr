namespace Optimisarr.Core.Deduplication;

/// <summary>
/// Where an exact copy waits in quarantine: inside its own library, so moving it can stay a rename
/// on one filesystem. Library scans skip this folder, or a quarantined copy would come back as media.
/// </summary>
public static class DedupeQuarantinePaths
{
    public const string FolderName = ".optimisarr-dedupe";

    public static string For(string root, Guid actionId, string path) =>
        Path.Combine(root, FolderName, actionId.ToString("N"), Path.GetRelativePath(root, path));

    public static bool IsInside(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == FolderName || relative.StartsWith(FolderName + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
