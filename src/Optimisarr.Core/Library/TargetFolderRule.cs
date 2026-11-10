namespace Optimisarr.Core.Library;

/// <summary>Why a folder may not collect a library's optimised files.</summary>
public enum TargetFolderRefusal
{
    None,
    InsideLibrary,
    WorkFolder,
    Quarantine,
}

/// <summary>A library's name and folder, as the rule needs them.</summary>
public sealed record LibraryFolder(string Name, string Path);

/// <summary>The rule's answer. <see cref="LibraryName"/> is set only for <see cref="TargetFolderRefusal.InsideLibrary"/>.</summary>
public sealed record TargetFolderVerdict(TargetFolderRefusal Refusal, string? LibraryName = null)
{
    public static readonly TargetFolderVerdict Allowed = new(TargetFolderRefusal.None);

    public bool IsAllowed => Refusal == TargetFolderRefusal.None;

    /// <summary>The camel-case name the API and the web client share.</summary>
    public string WireValue => Refusal switch
    {
        TargetFolderRefusal.InsideLibrary => "insideLibrary",
        TargetFolderRefusal.WorkFolder => "workFolder",
        TargetFolderRefusal.Quarantine => "quarantine",
        _ => "none",
    };

    public string Describe(string path) => Refusal switch
    {
        TargetFolderRefusal.InsideLibrary =>
            $"{path} is inside the {LibraryName} library. Optimisarr would find its own output on the next scan and try to optimise it again. Pick a folder outside every library.",
        TargetFolderRefusal.WorkFolder =>
            $"{path} is inside Optimisarr's working space. Files there are cleaned up automatically. Pick a different folder.",
        TargetFolderRefusal.Quarantine =>
            $"{path} is inside Quarantine. Files there are cleaned up automatically. Pick a different folder.",
        _ => $"{path} can be used.",
    };
}

/// <summary>
/// Where "save optimised files in another folder" may point. A folder inside a library would be
/// scanned, so Optimisarr would optimise its own output again. The working space and Quarantine
/// are cleaned up automatically, so a file saved there would be deleted. Pure: paths are compared
/// as text, so pass paths with any symbolic links already resolved (see <see cref="ResolveLinks"/>).
/// </summary>
public static class TargetFolderRule
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static TargetFolderVerdict Check(
        string path,
        IEnumerable<LibraryFolder> libraries,
        string workRoot,
        string quarantineRoot)
    {
        var library = libraries.FirstOrDefault(candidate => IsSameOrInside(path, candidate.Path));
        if (library is not null)
        {
            return new TargetFolderVerdict(TargetFolderRefusal.InsideLibrary, library.Name);
        }

        if (IsSameOrInside(path, workRoot))
        {
            return new TargetFolderVerdict(TargetFolderRefusal.WorkFolder);
        }

        return IsSameOrInside(path, quarantineRoot)
            ? new TargetFolderVerdict(TargetFolderRefusal.Quarantine)
            : TargetFolderVerdict.Allowed;
    }

    public static bool IsSameOrInside(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var candidate = Normalise(path);
        var container = Normalise(root);
        if (string.Equals(candidate, container, PathComparison))
        {
            return true;
        }

        var prefix = Path.EndsInDirectorySeparator(container) ? container : container + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, PathComparison);
    }

    /// <summary>
    /// The path with every symbolic link on it followed, so a link into a library cannot pass as a
    /// folder outside it. Parts that do not exist yet are kept as written.
    /// </summary>
    public static string ResolveLinks(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var resolved = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, part);
            try
            {
                var target = Directory.ResolveLinkTarget(resolved, returnFinalTarget: true);
                if (target is not null)
                {
                    resolved = target.FullName;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A part that cannot be read is kept as written; the text comparison still applies.
            }
        }

        return Normalise(resolved);
    }

    private static string Normalise(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
