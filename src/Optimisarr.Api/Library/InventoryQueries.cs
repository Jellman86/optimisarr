using Microsoft.EntityFrameworkCore;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

/// <summary>Which slice of the inventory to return, mirroring the Inventory page's filter chips.</summary>
public enum InventoryFilter
{
    All,
    Eligible,
    Skipped,
    Unprobed
}

/// <summary>One inventory row: the probed file detail plus its eligibility verdict (null when the file
/// has not been probed and so has no verdict yet).</summary>
public sealed record InventoryRow(MediaFileDto File, bool? Eligible, string? Reason);

/// <summary>Per-filter tallies so the UI can label its chips without fetching every row.</summary>
public sealed record InventoryCounts(int All, int Eligible, int Skipped, int Unprobed);

/// <summary>A page of inventory rows, the filtered total, and the per-filter tallies.</summary>
public sealed record InventoryPage(IReadOnlyList<InventoryRow> Items, int Total, InventoryCounts Counts);

/// <summary>
/// The Inventory view: each discovered file paired with the rule verdict that overlays it, filtered,
/// counted, and paged on the server so the browser fetches one page instead of the whole library. The
/// rule evaluation is pure logic over the probed inventory (no FFmpeg), so it stays cheap even for a
/// large library; only the page of rows crosses the wire.
/// </summary>
public sealed class InventoryQueries(OptimisarrDbContext db, CandidateService candidates)
{
    public async Task<InventoryPage> QueryAsync(
        int? libraryId,
        InventoryFilter filter,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        // Every file in scope (all detail fields), and the verdicts for the probed ones.
        var files = (await MediaQueries.QueryAsync(
                db, new MediaQuery { LibraryId = libraryId, Search = search }, cancellationToken))
            .Items;
        var verdicts = (await candidates.EvaluateAsync(libraryId, cancellationToken))
            .ToDictionary(candidate => candidate.MediaFileId);

        var rows = files
            .Select(file => verdicts.TryGetValue(file.Id, out var verdict)
                ? new InventoryRow(file, verdict.Eligible, verdict.Reason)
                : new InventoryRow(file, null, null))
            .ToList();

        var counts = new InventoryCounts(
            All: rows.Count,
            Eligible: rows.Count(row => row.Eligible == true),
            Skipped: rows.Count(row => row.Eligible == false),
            Unprobed: rows.Count(row => row.Eligible is null));

        var selected = rows.Where(row => Matches(row, filter)).ToList();

        var pageItems = pageSize > 0
            ? selected.Skip(Math.Max(page - 1, 0) * pageSize).Take(pageSize).ToList()
            : selected;

        return new InventoryPage(pageItems, selected.Count, counts);
    }

    private static bool Matches(InventoryRow row, InventoryFilter filter) => filter switch
    {
        InventoryFilter.Eligible => row.Eligible == true,
        InventoryFilter.Skipped => row.Eligible == false,
        InventoryFilter.Unprobed => row.Eligible is null,
        _ => true
    };
}
