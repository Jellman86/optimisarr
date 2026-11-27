namespace Optimisarr.Core.Library;

/// <summary>
/// Finds how a file beneath a library root is spelled on disk. A manager on a case-insensitive mount can
/// name a file in any letter case and still reach it, but the inventory is keyed on the exact path, so
/// recording the caller's spelling would give one file a second row and a second job. Each folder below
/// the root is read from its listing: an exact name wins, a single name that differs only in case is
/// taken, and two such names are refused rather than guessed between.
/// </summary>
public static class LibraryPathSpelling
{
    /// <summary>The path as the listings spell it, or null when it is not beneath the root or not there.</summary>
    /// <param name="entryNames">The names, not paths, of everything in one folder.</param>
    public static string? OnDisk(string root, string path, Func<string, IEnumerable<string>> entryNames)
    {
        var current = Path.TrimEndingDirectorySeparator(root);
        var rootPrefix = Path.EndsInDirectorySeparator(current) ? current : current + Path.DirectorySeparatorChar;
        if (path.Length <= rootPrefix.Length || !path.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var segment in path[rootPrefix.Length..].Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Match(entryNames(current), segment) is not { } name)
            {
                return null;
            }
            current = Path.Join(current, name);
        }
        return current;
    }

    /// <summary>The names in one folder, read from the filesystem; an unreadable folder has none.</summary>
    public static IEnumerable<string> ListFolder(string folder)
    {
        try
        {
            return [.. Directory.EnumerateFileSystemEntries(folder).Select(entry => Path.GetFileName(entry))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? Match(IEnumerable<string> names, string segment)
    {
        string? caseVariant = null;
        var variants = 0;
        foreach (var name in names)
        {
            if (string.Equals(name, segment, StringComparison.Ordinal))
            {
                return name;
            }
            if (string.Equals(name, segment, StringComparison.OrdinalIgnoreCase))
            {
                caseVariant = name;
                variants++;
            }
        }
        return variants == 1 ? caseVariant : null;
    }
}
