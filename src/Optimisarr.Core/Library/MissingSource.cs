namespace Optimisarr.Core.Library;

/// <summary>Whether a library folder can show what it contains.</summary>
public enum LibraryRootState
{
    Missing,
    Unreadable,
    Empty,
    Listed
}

public enum MissingSourceVerdict
{
    Present,
    /// <summary>The library is plainly there and only this file is gone: a media manager's upgrade, rename or removal.</summary>
    Replaced,
    /// <summary>The library itself cannot be seen, so the file's absence says nothing about the file.</summary>
    LibraryUnavailable
}

/// <summary>
/// Tells a file a media manager replaced while its job waited from a library that cannot be
/// reached. The first is routine and ends the job quietly; the second is an outage and must stay
/// loud, so a dropped share is never hidden behind a run of quiet skips (#610).
/// </summary>
public static class MissingSource
{
    public const string SkipReason =
        "The file is no longer there. A media manager may have upgraded or renamed it; the new file is picked up by the next scan or import.";

    public static LibraryRootState InspectLibraryRoot(string? libraryRoot)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            return LibraryRootState.Missing;
        }

        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(libraryRoot).GetEnumerator();
            return entries.MoveNext() ? LibraryRootState.Listed : LibraryRootState.Empty;
        }
        catch (DirectoryNotFoundException)
        {
            return LibraryRootState.Missing;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return LibraryRootState.Unreadable;
        }
    }

    public static (MissingSourceVerdict Verdict, string? UnavailableFolder) Inspect(string sourcePath, string? libraryRoot)
    {
        if (File.Exists(sourcePath)) return (MissingSourceVerdict.Present, null);
        // An unmounted share can leave an empty mount point where a previously scanned file lived.
        if (libraryRoot is null || !LibraryPathSafety.IsSafeDirectory(libraryRoot)
            || InspectLibraryRoot(libraryRoot) != LibraryRootState.Listed)
            return (MissingSourceVerdict.LibraryUnavailable, libraryRoot);

        var folder = LibraryPathSafety.IsSafeToPruneMissingFile(sourcePath, libraryRoot)
            ? FindUnavailableParent(sourcePath, libraryRoot)
            : Path.GetDirectoryName(sourcePath) ?? libraryRoot;
        return folder is null ? (MissingSourceVerdict.Replaced, null) : (MissingSourceVerdict.LibraryUnavailable, folder);
    }

    /// <summary>
    /// Checks the first existing parent of a safely missing path below a root already known to list.
    /// A missing season folder can sit below a mount point that still exists but is empty.
    /// </summary>
    public static string? FindUnavailableParent(string sourcePath, string libraryRoot)
    {
        for (var folder = Path.GetDirectoryName(sourcePath);
            folder is not null && Path.GetRelativePath(libraryRoot, folder) != ".";
            folder = Path.GetDirectoryName(folder))
        {
            var state = InspectLibraryRoot(folder);
            if (state != LibraryRootState.Missing)
                return state == LibraryRootState.Listed ? null : folder;
        }
        return null;
    }

    public static string LibraryUnavailableMessage(string? libraryRoot) =>
        $"The library folder {libraryRoot ?? "(none recorded)"} is missing, empty or cannot be read, so this file could not be reached. "
        + "Check that its drive or network share is mounted, then retry.";
}
