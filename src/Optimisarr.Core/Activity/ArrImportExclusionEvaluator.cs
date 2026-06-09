namespace Optimisarr.Core.Activity;

/// <summary>A title folder a connected manager is currently importing into.</summary>
/// <param name="ConnectionName">The Sonarr/Radarr connection's display name, used in the skip reason.</param>
/// <param name="Folder">The title's library folder, e.g. <c>/data/tv/Some Show</c>.</param>
public sealed record ArrActiveImport(string ConnectionName, string Folder);

/// <summary>
/// Pure policy for the optional "don't fight Sonarr/Radarr imports" exclusion. A
/// candidate file is held back from queueing while a connected manager is importing
/// into the folder that file lives in, so Optimisarr never transcodes a file an
/// import is about to move or replace. As with the activity-pause gate, an
/// unreachable manager contributes no active imports and therefore never blocks work.
/// </summary>
public static class ArrImportExclusionEvaluator
{
    /// <summary>
    /// Returns a human-readable reason if <paramref name="candidatePath"/> sits inside
    /// one of the active import folders, or null if it does not.
    /// </summary>
    public static string? ExclusionReason(string candidatePath, IReadOnlyList<ArrActiveImport> activeImports)
    {
        foreach (var import in activeImports)
        {
            if (IsWithin(candidatePath, import.Folder))
            {
                return $"{import.ConnectionName} is importing into {import.Folder}.";
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="path"/> is the folder itself or sits beneath it (segment-aware).</summary>
    public static bool IsWithin(string path, string folder)
    {
        var normalisedPath = Normalise(path);
        var normalisedFolder = Normalise(folder);
        if (normalisedFolder.Length == 0)
        {
            return false;
        }

        return normalisedPath.Equals(normalisedFolder, StringComparison.Ordinal)
            || normalisedPath.StartsWith(normalisedFolder + "/", StringComparison.Ordinal);
    }

    private static string Normalise(string value) => value.Replace('\\', '/').TrimEnd('/');
}
