using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Queue;
using Optimisarr.Api.Replacement;
using Optimisarr.Api.Setup;
using Optimisarr.Core.Library;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

/// <summary>
/// What the API says about a folder for "save optimised files in another folder".
/// <paramref name="Reason"/> is <c>none</c>, <c>insideLibrary</c>, <c>workFolder</c>,
/// <c>quarantine</c>, <c>missing</c> or <c>unwritable</c>; the web client translates it.
/// </summary>
internal sealed record TargetFolderCheck(
    string Path,
    bool Allowed,
    string Reason,
    string? LibraryName,
    string Message,
    long? AvailableBytes);

/// <summary>
/// Applies <see cref="TargetFolderRule"/> to the saved libraries and this server's working space
/// and Quarantine, then checks the folder exists and can be written. Read-only apart from the
/// empty probe file <see cref="PathAccessProbe"/> creates and removes.
/// </summary>
internal static class TargetFolderGuard
{
    public static async Task<TargetFolderCheck> CheckAsync(
        string path,
        OptimisarrDbContext db,
        IHostEnvironment environment,
        CancellationToken cancellationToken,
        LibraryFolder? beingSaved = null,
        int? beingSavedId = null)
    {
        var libraries = await SavedLibrariesAsync(db, beingSavedId, cancellationToken);
        if (beingSaved is not null)
        {
            libraries.Add(beingSaved);
        }

        var verdict = Judge(path, libraries, environment);
        if (!verdict.IsAllowed)
        {
            return new TargetFolderCheck(path, false, verdict.WireValue, verdict.LibraryName, verdict.Describe(path), null);
        }

        var (exists, _, writable) = PathAccessProbe.Probe(path);
        if (!exists)
        {
            return new TargetFolderCheck(path, false, "missing", null, $"Target folder does not exist: {path}", null);
        }

        if (!writable)
        {
            return new TargetFolderCheck(path, false, "unwritable", null,
                $"Optimisarr cannot write to {path}. Check the folder is mounted in the container and writable.", null);
        }

        return new TargetFolderCheck(path, true, "none", null, verdict.Describe(path),
            FileSystemEvidenceProbe.Probe(path).AvailableBytes);
    }

    /// <summary>
    /// The same rule for one finished file, checked again when it is about to be saved: a library
    /// can be added, or a folder linked, after the library's settings were saved.
    /// </summary>
    public static async Task<TargetFolderVerdict> CheckDestinationAsync(
        string destination,
        OptimisarrDbContext db,
        IHostEnvironment environment,
        CancellationToken cancellationToken) =>
        Judge(destination, await SavedLibrariesAsync(db, exceptId: null, cancellationToken), environment);

    private static TargetFolderVerdict Judge(string path, IEnumerable<LibraryFolder> libraries, IHostEnvironment environment) =>
        TargetFolderRule.Check(
            TargetFolderRule.ResolveLinks(path),
            libraries.Select(library => library with { Path = TargetFolderRule.ResolveLinks(library.Path) }),
            TargetFolderRule.ResolveLinks(WorkPaths.Resolve(environment)),
            TargetFolderRule.ResolveLinks(TrashPaths.Resolve(environment)));

    private static async Task<List<LibraryFolder>> SavedLibrariesAsync(
        OptimisarrDbContext db,
        int? exceptId,
        CancellationToken cancellationToken) =>
        await db.Libraries
            .AsNoTracking()
            .Where(library => exceptId == null || library.Id != exceptId)
            .OrderBy(library => library.Name)
            .Select(library => new LibraryFolder(library.Name, library.Path))
            .ToListAsync(cancellationToken);
}
