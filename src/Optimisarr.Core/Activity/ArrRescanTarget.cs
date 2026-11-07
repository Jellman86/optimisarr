namespace Optimisarr.Core.Activity;

/// <summary>A movie or series a manager lists, and the folder it keeps that title in.</summary>
public sealed record ArrTitleFolder(int Id, string Path);

/// <summary>
/// Works out which title a manager should rescan after a file changed. Optimisarr and the manager often
/// see the library under different mounts, so only folder names are compared, from the file's own folder
/// outwards. A title is named only when exactly one fits; a wrong rescan is harmless but pointless.
/// </summary>
public static class ArrRescanTarget
{
    public static int? TitleFor(string changedFilePath, IReadOnlyList<ArrTitleFolder> titles)
    {
        var segments = Folders(changedFilePath);
        if (segments.Length < 2) return null;
        var folders = segments[..^1];
        var candidates = titles.Select(title => (title.Id, Folders: Folders(title.Path)))
            .Where(title => title.Folders.Length > 0)
            .ToList();

        // Nearest folder first: an episode's season folder is passed over before its series folder.
        for (var depth = folders.Length - 1; depth >= 0; depth--)
        {
            var within = folders[..(depth + 1)];
            var named = candidates.Where(title => title.Folders[^1] == within[^1]).ToList();
            if (named.Count == 0) continue;
            if (named.Count == 1) return named[0].Id;

            var shared = named.Select(title => (title.Id, Shared: SharedEnding(title.Folders, within))).ToList();
            var best = shared.Max(title => title.Shared);
            var winners = shared.Where(title => title.Shared == best).ToList();
            return winners.Count == 1 ? winners[0].Id : null;
        }
        return null;
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
