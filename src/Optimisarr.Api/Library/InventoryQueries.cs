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

/// <summary>How to order the inventory rows before paging. Default keeps the stored path order; every
/// other order breaks ties by case-insensitive path and then id so pages never overlap or skip.</summary>
public enum InventorySort
{
    Default,
    NameAscending,
    NameDescending,
    SizeAscending,
    SizeDescending,
    VerdictAscending,
    VerdictDescending
}

/// <summary>One inventory row: the probed file detail plus its eligibility verdict (null when the file
/// has not been probed and so has no verdict yet). RetryJobId names the failed job to try again when
/// an earlier failure is what holds the file back.</summary>
public sealed record InventoryRow(MediaFileDto File, bool? Eligible, string? Reason, int? RetryJobId = null);

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
        InventorySort sort,
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
                ? new InventoryRow(file, verdict.Eligible, verdict.Reason, verdict.RetryJobId)
                : new InventoryRow(file, null, null))
            .ToList();

        var counts = new InventoryCounts(
            All: rows.Count,
            Eligible: rows.Count(row => row.Eligible == true),
            Skipped: rows.Count(row => row.Eligible == false),
            Unprobed: rows.Count(row => row.Eligible is null));

        var selected = Order(rows.Where(row => Matches(row, filter)), sort).ToList();

        var pageItems = pageSize > 0
            ? selected.Skip(Math.Max(page - 1, 0) * pageSize).Take(pageSize).ToList()
            : selected;

        return new InventoryPage(pageItems, selected.Count, counts);
    }

    /// <summary>The query-string spellings of <see cref="InventorySort"/>. Blank means the default order.</summary>
    public static readonly IReadOnlyDictionary<string, InventorySort> SortValues =
        new Dictionary<string, InventorySort>(StringComparer.OrdinalIgnoreCase)
        {
            ["name_asc"] = InventorySort.NameAscending,
            ["name_desc"] = InventorySort.NameDescending,
            ["size_asc"] = InventorySort.SizeAscending,
            ["size_desc"] = InventorySort.SizeDescending,
            ["verdict_asc"] = InventorySort.VerdictAscending,
            ["verdict_desc"] = InventorySort.VerdictDescending
        };

    public static bool TryParseSort(string? value, out InventorySort sort)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            sort = InventorySort.Default;
            return true;
        }
        return SortValues.TryGetValue(value.Trim(), out sort);
    }

    private static IEnumerable<InventoryRow> Order(IEnumerable<InventoryRow> rows, InventorySort sort)
    {
        var ordered = sort switch
        {
            InventorySort.NameAscending => rows.OrderBy(row => FileName(row.File.RelativePath), StringComparer.OrdinalIgnoreCase),
            InventorySort.NameDescending => rows.OrderByDescending(row => FileName(row.File.RelativePath), StringComparer.OrdinalIgnoreCase),
            InventorySort.SizeAscending => rows.OrderBy(row => row.File.SizeBytes),
            InventorySort.SizeDescending => rows.OrderByDescending(row => row.File.SizeBytes),
            InventorySort.VerdictAscending => rows.OrderBy(VerdictRank),
            InventorySort.VerdictDescending => rows.OrderByDescending(VerdictRank),
            _ => null
        };
        return ordered is null
            ? rows
            : ordered
                .ThenBy(row => row.File.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.File.Id);
    }

    // The inventory table displays only the filename, so "File" sorts by filename across both
    // Unix and Windows path separators, then breaks ties by full relative path and ID.
    private static string FileName(string relativePath)
    {
        var separator = relativePath.LastIndexOfAny(['/', '\\']);
        return separator >= 0 && separator < relativePath.Length - 1
            ? relativePath[(separator + 1)..]
            : relativePath;
    }

    // Eligible files are the ones the user can act on, so they lead an ascending verdict sort.
    private static int VerdictRank(InventoryRow row) => row.Eligible switch
    {
        true => 0,
        null => 1,
        false => 2
    };

    private static bool Matches(InventoryRow row, InventoryFilter filter) => filter switch
    {
        InventoryFilter.Eligible => row.Eligible == true,
        InventoryFilter.Skipped => row.Eligible == false,
        InventoryFilter.Unprobed => row.Eligible is null,
        _ => true
    };
}
