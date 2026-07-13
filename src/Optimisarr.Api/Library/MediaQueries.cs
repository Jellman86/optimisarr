using Microsoft.EntityFrameworkCore;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

/// <summary>One inventory row shaped for the Inventory UI and the media list API.</summary>
public sealed record MediaFileDto(
    int Id,
    int? LibraryId,
    string RelativePath,
    long SizeBytes,
    string Status,
    string MediaKind,
    string? Container,
    string? VideoCodec,
    int? Width,
    int? Height,
    double? DurationSeconds,
    string? AudioCodecs,
    string? AudioLanguages,
    int? AudioTrackCount,
    int? SubtitleTrackCount,
    DateTimeOffset? ProbedAt,
    string? ProbeError,
    // The Optimisarr version stamped into the file's metadata when it was produced by Optimisarr, or
    // null for an original/foreign file. Surfaced so the inventory can tell an optimised output apart
    // from a source and so a collision-style issue is diagnosable from the API.
    string? OptimisedMarker);

/// <summary>
/// Filters and paging for <see cref="MediaQueries.QueryAsync"/>. All optional; <see cref="PageSize"/>
/// of 0 disables paging and returns every match. <see cref="Search"/> matches a case-insensitive
/// substring of the relative path.
/// </summary>
public sealed record MediaQuery
{
    public int? LibraryId { get; init; }
    public MediaFileStatus? Status { get; init; }
    public string? Search { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; }
}

/// <summary>A page of inventory rows plus the total number of matches before paging.</summary>
public sealed record MediaQueryResult(IReadOnlyList<MediaFileDto> Items, int Total);

public static class MediaQueries
{
    /// <summary>
    /// Lists probed/discovered files ordered by relative path, optionally filtered and paged. Unlike
    /// the job feed, the path is a sortable column, so filtering, counting, ordering, and paging all
    /// run in the database — keeping it responsive for libraries with tens of thousands of files.
    /// </summary>
    public static async Task<MediaQueryResult> QueryAsync(
        OptimisarrDbContext db,
        MediaQuery filter,
        CancellationToken cancellationToken)
    {
        var query = db.MediaFiles.AsNoTracking();

        if (filter.LibraryId is { } libraryId)
        {
            query = query.Where(file => file.LibraryId == libraryId);
        }
        if (filter.Status is { } status)
        {
            query = query.Where(file => file.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            query = query.Where(file => EF.Functions.Like(file.RelativePath, $"%{term}%"));
        }

        var total = await query.CountAsync(cancellationToken);

        var ordered = query.OrderBy(file => file.RelativePath);
        var paged = filter.PageSize > 0
            ? ordered.Skip(Math.Max(filter.Page - 1, 0) * filter.PageSize).Take(filter.PageSize)
            : (IQueryable<MediaFile>)ordered;

        var items = await paged
            .Select(file => new MediaFileDto(
                file.Id,
                file.LibraryId,
                file.RelativePath,
                file.SizeBytes,
                file.Status.ToString(),
                file.MediaKind.ToString(),
                file.Container,
                file.VideoCodec,
                file.Width,
                file.Height,
                file.DurationSeconds,
                file.AudioCodecs,
                file.AudioLanguages,
                file.AudioTrackCount,
                file.SubtitleTrackCount,
                file.ProbedAt,
                file.ProbeError,
                file.OptimisedMarker))
            .ToListAsync(cancellationToken);

        return new MediaQueryResult(items, total);
    }
}
