using Microsoft.EntityFrameworkCore;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

/// <summary>
/// One-time, idempotent migration of the original single <c>library.root</c>
/// setting into the multi-library model. If a root was configured before
/// libraries existed, it becomes a default library and any media files already
/// discovered under it are linked to it.
/// </summary>
internal static class LibrarySeeder
{
    public static async Task MigrateLegacyLibraryRootAsync(
        OptimisarrDbContext db,
        SettingsStore settings,
        CancellationToken cancellationToken)
    {
        if (await db.Libraries.AnyAsync(cancellationToken))
        {
            return;
        }

        var legacyRoot = await settings.GetLibraryRootAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(legacyRoot))
        {
            return;
        }

        var library = new Data.Library
        {
            Name = "Library",
            Path = legacyRoot,
            MediaType = MediaType.Other,
            RuleProfile = RuleProfile.ConservativeHevc,
            Enabled = true
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync(cancellationToken);

        // Link pre-existing media files (discovered before libraries existed,
        // so LibraryId is null) to the migrated library.
        await db.MediaFiles
            .Where(file => file.LibraryId == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(file => file.LibraryId, library.Id),
                cancellationToken);
    }
}
