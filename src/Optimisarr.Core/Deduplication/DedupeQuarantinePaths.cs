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

    /// <summary>Whether any folder between the root and the path is a quarantine folder, such as a child library's.</summary>
    public static bool IsInside(string root, string path) =>
        Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Contains(FolderName, StringComparer.Ordinal);
}
