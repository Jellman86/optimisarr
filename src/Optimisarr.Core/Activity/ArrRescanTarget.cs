namespace Optimisarr.Core.Activity;

/// <summary>A movie or series a manager lists, and the folder it keeps that title in.</summary>
public sealed record ArrTitleFolder(int Id, string Path);

/// <summary>
/// Works out which title a manager should rescan after a file changed. Optimisarr and the manager often
/// see the library under different mounts, so folder names are compared, but only where the manager keeps
/// a title's files: Radarr puts a movie's file in the movie folder, and Sonarr puts an episode in the series
/// folder or one folder below it, such as a season. A title is named only when exactly one fits there, so a
/// series with a "Specials" folder never sends the rescan to a series called Specials.
/// </summary>
public static class ArrRescanTarget
{
    /// <summary>A movie file sits directly in its movie folder.</summary>
    public const int MovieFolderDepth = 1;

    /// <summary>An episode sits in its series folder or one folder below it.</summary>
    public const int SeriesFolderDepth = 2;

    public static int? TitleFor(string changedFilePath, IReadOnlyList<ArrTitleFolder> titles, int folderDepth)
    {
        var segments = Folders(changedFilePath);
        if (segments.Length < 2) return null;
        var folders = segments[..^1];
        var candidates = titles.Select(title => (title.Id, Folders: Folders(title.Path)))
            .Where(title => title.Folders.Length > 0)
            .ToList();

        var found = new HashSet<int>();
        for (var up = 0; up < folderDepth && up < folders.Length; up++)
        {
            var within = folders[..^up];
            var named = candidates.Where(title => title.Folders[^1] == within[^1]).ToList();
            if (named.Count == 0) continue;

            // Two titles with the same folder name, as in separate HD and 4K root folders, are told apart
            // by how much of the path above them also matches.
            var shared = named.Select(title => (title.Id, Shared: SharedEnding(title.Folders, within))).ToList();
            var best = shared.Max(title => title.Shared);
            var winners = shared.Where(title => title.Shared == best).ToList();
            if (winners.Count > 1) return null;
            found.Add(winners[0].Id);
        }
        return found.Count == 1 ? found.Single() : null;
    }

    private static string[] Folders(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return [.. parts.Select(ArrTrackedFileMatcher.Comparable).Where(part => part.Length > 0)];
    }

    private static int SharedEnding(string[] left, string[] right)
    {
        var shared = 0;
        while (shared < left.Length && shared < right.Length && left[^(shared + 1)] == right[^(shared + 1)]) shared++;
        return shared;
    }
}
