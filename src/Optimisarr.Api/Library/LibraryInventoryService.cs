using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

public sealed record ScanSummary(int Discovered, int Added, int Updated, int Removed, int SkippedUnsettled);

/// <summary>
/// Orchestrates filesystem discovery and ffprobe inspection with the database.
/// Scans are idempotent: re-running against an unchanged library produces no
/// new rows and no changes, matching the repository's database standards.
/// </summary>
public sealed class LibraryInventoryService(
    OptimisarrDbContext db,
    LibraryScanner scanner,
    MediaProbeService probe,
    ImageMarkerService imageMarker)
{
    /// <summary>Scans every enabled library and returns the combined summary.</summary>
    public async Task<ScanSummary> ScanEnabledAsync(CancellationToken cancellationToken)
    {
        var libraries = await db.Libraries
            .Where(library => library.Enabled)
            .ToListAsync(cancellationToken);

        var total = new ScanSummary(0, 0, 0, 0, 0);
        foreach (var library in libraries)
        {
            var summary = await ScanAsync(library, cancellationToken);
            total = new ScanSummary(
                total.Discovered + summary.Discovered,
                total.Added + summary.Added,
                total.Updated + summary.Updated,
                total.Removed + summary.Removed,
                total.SkippedUnsettled + summary.SkippedUnsettled);
        }

        return total;
    }

    public async Task<ScanSummary> ScanAsync(Data.Library library, CancellationToken cancellationToken)
    {
        // Discover the file types this library's media type actually holds (audio for Music,
        // images for Photo, video for Film/TV, all for Other), not just video.
        var options = new LibraryScanOptions { Extensions = LibraryScanner.ExtensionsFor(library.MediaType) };
        var result = scanner.Scan(library.Path, options, DateTimeOffset.UtcNow);

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
                file.MediaKind = MediaKind.Unknown;
                file.Container = null;
                file.DurationSeconds = null;
                file.VideoCodec = null;
                file.VideoProfile = null;
                file.IsVariableFrameRate = null;
                file.AttachedPictureCount = 0;
                file.Width = null;
                file.Height = null;
                file.AudioCodecs = null;
                file.AudioLanguages = null;
                file.AudioTrackCount = null;
                file.MaxAudioChannels = 0;
                file.AudioBitrateKbps = null;
                file.SubtitleTrackCount = null;
                file.SubtitleLanguages = null;
                file.IsHdr = false;
                file.IsDolbyVision = false;
                file.OptimisedMarker = null;
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

        var removed = await PruneVanishedFilesAsync(existingByPath, result.Files, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return new ScanSummary(result.Files.Count, added, updated, removed, result.SkippedUnsettled);
    }

    /// <summary>
    /// Retires inventory rows whose file has vanished — e.g. Radarr/Sonarr upgraded a release and
    /// renamed it, leaving the old path dangling. Without this the stale row lingers as a phantom
    /// candidate and any job enqueued against it fails with "No such file or directory". A row is
    /// pruned only when the file is genuinely gone from disk (a settled file merely skipped this scan
    /// — e.g. still within its settling window — is kept), and never when it has replacement history,
    /// whose rollback records must survive (and whose job rows are FK-restricted from deletion).
    /// Idempotent: a scan of an unchanged library removes nothing.
    /// </summary>
    private async Task<int> PruneVanishedFilesAsync(
        IReadOnlyDictionary<string, MediaFile> existingByPath,
        IReadOnlyList<ScannedFile> scannedFiles,
        CancellationToken cancellationToken)
    {
        var scannedPaths = scannedFiles.Select(file => file.AbsolutePath).ToHashSet(StringComparer.Ordinal);

        var candidates = existingByPath.Values
            .Where(file => !scannedPaths.Contains(file.Path) && !File.Exists(file.Path))
            .ToList();
        if (candidates.Count == 0)
        {
            return 0;
        }

        var protectedIds = await db.Replacements
            .Where(replacement => replacement.MediaFileId != 0)
            .Select(replacement => replacement.MediaFileId)
            .Distinct()
            .ToHashSetAsync(cancellationToken);

        var prunable = candidates.Where(file => !protectedIds.Contains(file.Id)).ToList();
        if (prunable.Count == 0)
        {
            return 0;
        }

        // Removing the media file cascades to its jobs (the failed/queued work for a file that no
        // longer exists is meaningless); rows with replacement history were excluded above.
        db.MediaFiles.RemoveRange(prunable);
        return prunable.Count;
    }

    /// <summary>
    /// Probes files still in the <see cref="MediaFileStatus.Discovered"/> state — optionally
    /// limited to one library — up to <paramref name="maxFiles"/>, returning how many were probed.
    /// Scanning only records that a file exists; candidate evaluation needs its codec/kind/size,
    /// which come from ffprobe, so this is the bridge between discovery and eligibility. A probe
    /// failure is recorded as <see cref="MediaFileStatus.ProbeFailed"/> and not retried, so
    /// repeated calls converge.
    /// </summary>
    public async Task<int> ProbePendingAsync(int? libraryId, int maxFiles, CancellationToken cancellationToken)
    {
        if (maxFiles <= 0)
        {
            return 0;
        }

        var query = db.MediaFiles.Where(file => file.Status == MediaFileStatus.Discovered);
        if (libraryId is not null)
        {
            query = query.Where(file => file.LibraryId == libraryId);
        }

        var ids = await query
            .OrderBy(file => file.Id)
            .Take(maxFiles)
            .Select(file => file.Id)
            .ToListAsync(cancellationToken);

        var probed = 0;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProbeAsync(id, cancellationToken);
            probed++;
        }

        return probed;
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
            file.MediaKind = result.MediaKind;
            file.Container = result.Container;
            file.DurationSeconds = result.DurationSeconds;
            file.VideoCodec = result.VideoCodec;
            file.VideoProfile = result.VideoProfile;
            file.Width = result.Width;
            file.Height = result.Height;
            file.FrameCount = result.FrameCount;
            file.IsVariableFrameRate = result.IsVariableFrameRate;
            file.PixelFormat = result.PixelFormat;
            file.BitsPerRawSample = result.BitsPerRawSample;
            file.AttachedPictureCount = result.AttachedPictureCount;
            file.AudioCodecs = result.AudioCodecs.Count > 0 ? string.Join(", ", result.AudioCodecs) : null;
            // "und" stands in for an untagged track so the stored order still lines up with
            // the file's audio-relative stream indexes.
            file.AudioLanguages = result.AudioTracks.Count > 0
                ? string.Join(", ", result.AudioTracks.Select(track => track.Language ?? "und"))
                : null;
            file.AudioTrackCount = result.AudioTrackCount;
            file.MaxAudioChannels = result.MaxAudioChannels;
            file.AudioBitrateKbps = result.AudioBitrateKbps;
            file.SubtitleTrackCount = result.SubtitleTrackCount;
            file.SubtitleLanguages = result.SubtitleLanguages.Count > 0
                ? string.Join(", ", result.SubtitleLanguages.Select(language => language ?? "und"))
                : null;
            file.IsHdr = result.IsHdr;
            file.IsDolbyVision = result.IsDolbyVision;
            // Images carry their marker in EXIF/XMP (ffprobe doesn't surface it), so read it back
            // with exiftool when the container probe found none.
            file.OptimisedMarker = result.OptimisedMarker
                ?? (result.MediaKind == MediaKind.Image
                    ? await imageMarker.ReadAsync(file.Path, cancellationToken)
                    : null);
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
