namespace Optimisarr.Core.Library;

/// <summary>What a library's folder looked like when one of its queued files was found missing.</summary>
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

    // An empty library counts as unavailable: an unmounted network share usually leaves its
    // mount point behind as an empty folder, and a real library is never empty while it has a
    // queued file in it.
    public static MissingSourceVerdict Classify(bool sourceExists, LibraryRootState libraryRoot) =>
        sourceExists ? MissingSourceVerdict.Present
        : libraryRoot == LibraryRootState.Listed ? MissingSourceVerdict.Replaced
        : MissingSourceVerdict.LibraryUnavailable;

    public static LibraryRootState InspectLibraryRoot(string? libraryRoot)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
        {
            return LibraryRootState.Missing;
        }

        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(libraryRoot).GetEnumerator();
            return entries.MoveNext() ? LibraryRootState.Listed : LibraryRootState.Empty;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return LibraryRootState.Unreadable;
        }
    }

    public static MissingSourceVerdict Inspect(string sourcePath, string? libraryRoot) =>
        File.Exists(sourcePath)
            ? MissingSourceVerdict.Present
            : Classify(sourceExists: false, InspectLibraryRoot(libraryRoot));

    public static string LibraryUnavailableMessage(string? libraryRoot) =>
        $"The library folder {libraryRoot ?? "(none recorded)"} is missing, empty or cannot be read, so this file could not be reached. "
        + "Check that its drive or network share is mounted, then retry.";
}
