namespace Optimisarr.Core.Library;

/// <summary>
/// Point-in-time checks for library paths. Refuses symbolic links and Windows reparse points
/// at the configured root and below it. Ancestors above an operator-configured root are trusted.
/// These checks do not pin handles or prevent a concurrent filesystem writer changing the path.
/// </summary>
public static class LibraryPathSafety
{
    public const string Refusal = "The source path is outside its library, contains a symbolic link or reparse point, or cannot be checked safely. Use a regular file in the library and re-scan.";

    public static bool IsSafeDirectory(string root)
    {
        try
        {
            return Path.IsPathFullyQualified(root) && !HasParentTraversal(root)
                && IsRegularDirectory(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        }
        catch (Exception error) when (IsPathError(error)) { return false; }
    }

    public static bool IsSafeFile(string path, string? libraryRoot = null, bool allowMissingLeaf = false) =>
        CheckFilePath(path, libraryRoot, allowMissingLeaf, allowMissingParents: false);

    /// <summary>A skipped path is only prunable when its absence is visible through unlinked ancestors.</summary>
    public static bool IsSafeToPruneMissingFile(string path, string libraryRoot) =>
        !File.Exists(path) && CheckFilePath(path, libraryRoot, allowMissingLeaf: true, allowMissingParents: true);

    private static bool CheckFilePath(string path, string? libraryRoot, bool allowMissingLeaf, bool allowMissingParents)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || HasParentTraversal(path)
                || libraryRoot is not null && (!Path.IsPathFullyQualified(libraryRoot) || HasParentTraversal(libraryRoot))) return false;
            var fullPath = Path.GetFullPath(path);
            // Legacy inventory without a library has no narrower trusted anchor. Check all its
            // components; associating it with a library supplies the normal operator-owned root.
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot ?? Path.GetPathRoot(fullPath)!));
            var relative = Path.GetRelativePath(root, fullPath);
            if (relative == "." || relative == ".." || Path.IsPathRooted(relative)
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return false;
            if (!IsRegularDirectory(root)) return false;

            var components = relative.Split(Path.DirectorySeparatorChar);
            var current = root;
            for (var index = 0; index < components.Length; index++)
            {
                current = Path.Combine(current, components[index]);
                var isLeaf = index == components.Length - 1;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (Exception error) when ((error is FileNotFoundException or DirectoryNotFoundException)
                    && (isLeaf && allowMissingLeaf || allowMissingParents)) { return true; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (isLeaf && isDirectory || !isLeaf && !isDirectory) return false;
            }
            return true;
        }
        catch (Exception error) when (IsPathError(error)) { return false; }
    }

    private static bool IsRegularDirectory(string path) =>
        (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;

    private static bool HasParentTraversal(string path) =>
        path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]).Contains("..");

    private static bool IsPathError(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}
