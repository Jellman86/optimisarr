using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

public sealed record ScanSummary(int Discovered, int Added, int Updated, int Removed, int SkippedUnsettled)
{
    public IReadOnlyList<string> UnavailableFolders { get; init; } = [];
}

/// <summary>
/// One writer at a time per library's inventory. A scan reads the library's rows, then adds what is new;
/// a webhook adding the same new file in between would make the scan's save fail on the unique path.
/// </summary>
public sealed class LibraryInventoryLocks
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> AcquireAsync(int libraryId, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(libraryId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Release(gate);
    }

    /// <summary>Null when the library stayed busy for all of <paramref name="wait"/>.</summary>
    public async Task<IDisposable?> TryAcquireAsync(int libraryId, TimeSpan wait, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(libraryId, _ => new SemaphoreSlim(1, 1));
        return await gate.WaitAsync(wait, cancellationToken) ? new Release(gate) : null;
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}

/// <summary>
/// Orchestrates filesystem discovery and ffprobe inspection with the database.
/// Scans are idempotent: re-running against an unchanged library produces no
/// new rows and no changes, matching the repository's database standards.
/// </summary>
public sealed class LibraryInventoryService(
    OptimisarrDbContext db,
    LibraryScanner scanner,
    IMediaProbeService probe,
    ImageMarkerService imageMarker,
    MusicTagService? musicTags = null,
    LibraryInventoryLocks? locks = null)
{
    private readonly LibraryInventoryLocks _locks = locks ?? new LibraryInventoryLocks();

    /// <summary>Scans every enabled library and returns the combined summary.</summary>
    public async Task<ScanSummary> ScanEnabledAsync(CancellationToken cancellationToken)
    {
        var libraries = await db.Libraries
            .Where(library => library.Enabled)
            .ToListAsync(cancellationToken);

        var total = new ScanSummary(0, 0, 0, 0, 0);
        var unavailableFolders = new List<string>();
        foreach (var library in libraries)
        {
            var summary = await ScanAsync(library, cancellationToken);
            total = new ScanSummary(
                total.Discovered + summary.Discovered,
                total.Added + summary.Added,
                total.Updated + summary.Updated,
                total.Removed + summary.Removed,
                total.SkippedUnsettled + summary.SkippedUnsettled);
            unavailableFolders.AddRange(summary.UnavailableFolders);
        }

        return total with { UnavailableFolders = unavailableFolders };
    }

    public async Task<ScanSummary> ScanAsync(Data.Library library, CancellationToken cancellationToken)
    {
        using var held = await _locks.AcquireAsync(library.Id, cancellationToken);
        if (!CanList(library.Path)) return UnavailableRoot(library.Path);

        // Discover the file types this library's media type actually holds (audio for Music,
        // images for Photo, video for Film/TV, all for Other), not just video.
        var options = new LibraryScanOptions { Extensions = LibraryScanner.ExtensionsFor(library.MediaType) };
        LibraryScanResult result;
        try
        {
            result = scanner.Scan(library.Path, options, DateTimeOffset.UtcNow);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (CanList(library.Path)) throw;
            return UnavailableRoot(library.Path);
        }

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
                db.MediaFiles.Add(NewMediaFile(library, scanned, now));
                added++;
            }
            else if (ApplyScan(file, scanned, now))
            {
                updated++;
            }
        }

        var unavailableFolders = new HashSet<string>(StringComparer.Ordinal);
        var removed = 0;
        if (CanList(library.Path))
            removed = await PruneVanishedFilesAsync(existingByPath, result.Files, library.Path, unavailableFolders, cancellationToken);
        else
            unavailableFolders.Add(library.Path);

        await db.SaveChangesAsync(cancellationToken);

        return new ScanSummary(result.Files.Count, added, updated, removed, result.SkippedUnsettled)
        {
            UnavailableFolders = [.. unavailableFolders.Order(StringComparer.Ordinal)]
        };
    }

    private static bool CanList(string path) =>
        LibraryPathSafety.IsSafeDirectory(path) && MissingSource.InspectLibraryRoot(path) == LibraryRootState.Listed;

    private static ScanSummary UnavailableRoot(string path) =>
        new(0, 0, 0, 0, 0) { UnavailableFolders = [path] };

    /// <summary>
    /// Records one settled file exactly as a scan of <paramref name="library"/> would, so a file a manager
    /// reported lands in the inventory in the same state the next scan would have left it. Unchanged
    /// files write nothing. The caller has already chosen the library that holds the file's row, if any.
    /// Waits at most <paramref name="lockWait"/> for a scan of the library to finish, and returns null
    /// instead of waiting out a long scan.
    /// </summary>
    public async Task<MediaFile?> TryRecordScannedFileAsync(
        Data.Library library, ScannedFile scanned, TimeSpan lockWait, CancellationToken cancellationToken)
    {
        using var held = await _locks.TryAcquireAsync(library.Id, lockWait, cancellationToken);
        if (held is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.Path == scanned.AbsolutePath, cancellationToken);
        if (file is null)
        {
            file = NewMediaFile(library, scanned, now);
            db.MediaFiles.Add(file);
        }
        else if (file.LibraryId != library.Id)
        {
            throw new InvalidOperationException("The file's inventory row belongs to another library.");
        }
        else
        {
            ApplyScan(file, scanned, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        return file;
    }

    private static MediaFile NewMediaFile(Data.Library library, ScannedFile scanned, DateTimeOffset now) => new()
    {
        LibraryId = library.Id,
        Path = scanned.AbsolutePath,
        RelativePath = scanned.RelativePath,
        SizeBytes = scanned.SizeBytes,
        ModifiedAt = scanned.ModifiedAt,
        HardLinkCount = scanned.HardLinkCount,
        DiscoveredAt = now,
        UpdatedAt = now,
        Status = MediaFileStatus.Discovered
    };

    /// <summary>Brings an existing row up to date with what a scan saw; true when anything changed.</summary>
    private static bool ApplyScan(MediaFile file, ScannedFile scanned, DateTimeOffset now)
    {
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
            file.FieldOrder = null;
            file.IsVariableFrameRate = null;
            file.AttachedPictureCount = 0;
            file.TrimOnlyTagRefusal = null;
            file.Width = null;
            file.Height = null;
            file.AudioCodecs = null;
            file.AudioLanguages = null;
            file.AudioTrackCount = null;
            file.MaxAudioChannels = 0;
            file.AudioBitrateKbps = null;
            file.SubtitleTrackCount = null;
            file.SubtitleLanguages = null;
            file.AudioTrackBytes = null;
            file.SubtitleTrackBytes = null;
            file.IsHdr = false;
            file.IsDolbyVision = false;
            file.OptimisedMarker = null;
            file.ProbedAt = null;
            file.ProbeError = null;
        }

        // The link count moves independently of the content: a download client can add or drop
        // a link without changing a byte or the modified time. Refresh it on every scan, but
        // only count a row as updated when the value actually differs, so a repeat scan of an
        // unchanged library still writes nothing.
        var linkCountChanged = file.HardLinkCount != scanned.HardLinkCount;
        if (linkCountChanged)
        {
            file.HardLinkCount = scanned.HardLinkCount;
        }

        if (contentChanged || linkCountChanged || file.RelativePath != scanned.RelativePath)
        {
            file.RelativePath = scanned.RelativePath;
            file.UpdatedAt = now;
            return true;
        }

        return false;
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
        string libraryRoot,
        HashSet<string> unavailableFolders,
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

        // The share can disappear while the database is returning the history.
        if (!CanList(libraryRoot))
        {
            unavailableFolders.Add(libraryRoot);
            return 0;
        }

        var prunable = new List<MediaFile>();
        foreach (var file in candidates)
        {
            if (!LibraryPathSafety.IsSafeToPruneMissingFile(file.Path, libraryRoot))
                continue;
            if (MissingSource.FindUnavailableParent(file.Path, libraryRoot) is { } unavailable)
                unavailableFolders.Add(unavailable);
            else if (!protectedIds.Contains(file.Id))
                prunable.Add(file);
        }
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
        var file = await db.MediaFiles.Include(f => f.Library)
            .FirstOrDefaultAsync(f => f.Id == mediaFileId, cancellationToken);
        if (file is null)
        {
            return null;
        }

        var result = LibraryPathSafety.IsSafeFile(file.Path, file.Library?.Path)
            ? await probe.ProbeAsync(file.Path, cancellationToken)
            : MediaProbeResult.Failure(LibraryPathSafety.Refusal);
        file.ProbedAt = DateTimeOffset.UtcNow;
        file.UpdatedAt = DateTimeOffset.UtcNow;

        if (result.Success)
        {
            file.Status = MediaFileStatus.Probed;
            file.MediaKind = result.MediaKind;
            file.Container = result.Container;
            file.DurationSeconds = MediaTimelineDuration.Resolve(
                result.MediaKind,
                result.VideoDurationSeconds,
                result.DurationSeconds);
            file.VideoCodec = result.VideoCodec;
            file.VideoProfile = result.VideoProfile;
            file.FieldOrder = result.FieldOrder;
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
            file.AudioTrackBytes = TrackSizes.Format(result.AudioTrackBytes);
            file.SubtitleTrackBytes = TrackSizes.Format(result.SubtitleTrackBytes);
            file.IsHdr = result.IsHdr;
            file.IsDolbyVision = result.IsDolbyVision;
            // Images carry their marker in EXIF/XMP (ffprobe doesn't surface it), so read it back
            // with exiftool when the container probe found none.
            // Read now so Candidates can say why a file is not trimmed before any job is queued.
            file.TrimOnlyTagRefusal = result.MediaKind == MediaKind.Audio && musicTags is not null && AudioTrimOnly.ReadsTags(file.Path)
                ? TrimOnlyTags.Refusal(await ReadTagsAsync(musicTags, file.Path, cancellationToken))
                : null;
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

    // A failed tag read refuses the trim, never the probe: a file left at Discovered would be picked
    // again by every probe sweep, ahead of the rest of the library.
    private static async Task<MusicTagRead> ReadTagsAsync(MusicTagService musicTags, string path, CancellationToken cancellationToken)
    {
        try
        {
            return await musicTags.ReadAsync(path, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return MusicTagRead.Unreadable($"reading them failed: {error.Message}");
        }
    }
}
