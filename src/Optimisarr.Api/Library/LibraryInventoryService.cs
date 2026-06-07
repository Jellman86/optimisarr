using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Library;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

public sealed record ScanSummary(int Discovered, int Added, int Updated, int SkippedUnsettled);

/// <summary>
/// Orchestrates filesystem discovery and ffprobe inspection with the database.
/// Scans are idempotent: re-running against an unchanged library produces no
/// new rows and no changes, matching the repository's database standards.
/// </summary>
public sealed class LibraryInventoryService(
    OptimisarrDbContext db,
    LibraryScanner scanner,
    MediaProbeService probe)
{
    /// <summary>Scans every enabled library and returns the combined summary.</summary>
    public async Task<ScanSummary> ScanEnabledAsync(CancellationToken cancellationToken)
    {
        var libraries = await db.Libraries
            .Where(library => library.Enabled)
            .ToListAsync(cancellationToken);

        var total = new ScanSummary(0, 0, 0, 0);
        foreach (var library in libraries)
        {
            var summary = await ScanAsync(library, cancellationToken);
            total = new ScanSummary(
                total.Discovered + summary.Discovered,
                total.Added + summary.Added,
                total.Updated + summary.Updated,
                total.SkippedUnsettled + summary.SkippedUnsettled);
        }

        return total;
    }

    public async Task<ScanSummary> ScanAsync(Data.Library library, CancellationToken cancellationToken)
    {
        var result = scanner.Scan(library.Path, new LibraryScanOptions(), DateTimeOffset.UtcNow);

        var existingByPath = await db.MediaFiles
            .Where(file => file.LibraryId == library.Id)
            .ToDictionaryAsync(file => file.Path, cancellationToken);

        var added = 0;
        var updated = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var scanned in result.Files)
        {
            if (!existingByPath.TryGetValue(scanned.AbsolutePath, out var file))
            {
                db.MediaFiles.Add(new MediaFile
                {
                    LibraryId = library.Id,
                    Path = scanned.AbsolutePath,
                    RelativePath = scanned.RelativePath,
                    SizeBytes = scanned.SizeBytes,
                    ModifiedAt = scanned.ModifiedAt,
                    DiscoveredAt = now,
                    UpdatedAt = now,
                    Status = MediaFileStatus.Discovered
                });
                added++;
                continue;
            }

            // A file whose size or modified time changed must be re-probed, so
            // clear any stale probe results and reset it to Discovered.
            var contentChanged = file.SizeBytes != scanned.SizeBytes || file.ModifiedAt != scanned.ModifiedAt;
            if (contentChanged)
            {
                file.SizeBytes = scanned.SizeBytes;
                file.ModifiedAt = scanned.ModifiedAt;
                file.Status = MediaFileStatus.Discovered;
                file.Container = null;
                file.DurationSeconds = null;
                file.VideoCodec = null;
                file.Width = null;
                file.Height = null;
                file.AudioCodecs = null;
                file.AudioTrackCount = null;
                file.SubtitleTrackCount = null;
                file.ProbedAt = null;
                file.ProbeError = null;
            }

            if (contentChanged || file.RelativePath != scanned.RelativePath)
            {
                file.RelativePath = scanned.RelativePath;
                file.UpdatedAt = now;
                updated++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        return new ScanSummary(result.Files.Count, added, updated, result.SkippedUnsettled);
    }

    public async Task<MediaFile?> ProbeAsync(int mediaFileId, CancellationToken cancellationToken)
    {
        var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.Id == mediaFileId, cancellationToken);
        if (file is null)
        {
            return null;
        }

        var result = await probe.ProbeAsync(file.Path, cancellationToken);
        file.ProbedAt = DateTimeOffset.UtcNow;
        file.UpdatedAt = DateTimeOffset.UtcNow;

        if (result.Success)
        {
            file.Status = MediaFileStatus.Probed;
            file.Container = result.Container;
            file.DurationSeconds = result.DurationSeconds;
            file.VideoCodec = result.VideoCodec;
            file.Width = result.Width;
            file.Height = result.Height;
            file.AudioCodecs = result.AudioCodecs.Count > 0 ? string.Join(", ", result.AudioCodecs) : null;
            file.AudioTrackCount = result.AudioTrackCount;
            file.SubtitleTrackCount = result.SubtitleTrackCount;
            file.ProbeError = null;
        }
        else
        {
            file.Status = MediaFileStatus.ProbeFailed;
            file.ProbeError = result.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
        return file;
    }
}
