using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

/// <summary>One side (original or encoded) of a preview comparison, from a fresh probe.</summary>
public sealed record MediaSideStats(
    long? SizeBytes,
    string? Container,
    string? VideoCodec,
    int? Width,
    int? Height,
    double? DurationSeconds,
    int? AudioChannels,
    string? AudioCodec,
    int? AudioBitrateKbps);

/// <summary>A settings preview's state and, once finished, the original-vs-encoded comparison.</summary>
public sealed record PreviewComparison(
    int JobId,
    int MediaFileId,
    string MediaKind,
    string Status,
    double Progress,
    string? ErrorMessage,
    MediaSideStats? Original,
    MediaSideStats? Encoded,
    double? SavingPercent,
    bool Clipped,
    bool? VerificationPassed,
    string? VerificationReportJson);

/// <summary>
/// Drives throwaway "preview" jobs: a one-off transcode of a single file with its library's
/// resolved settings, so the operator can compare the original against the encoded result before
/// committing. Reuses the queue worker; nothing here ever replaces or deletes an original.
/// </summary>
public sealed class PreviewService(
    OptimisarrDbContext db,
    MediaProbeService probe,
    QueueDispatcher dispatcher,
    IHostEnvironment environment)
{
    private readonly string _workRoot = WorkPaths.Resolve(environment);

    /// <summary>Queues a fresh preview for a file, replacing any earlier preview of it.</summary>
    public async Task<int?> CreateAsync(int mediaFileId, CancellationToken cancellationToken)
    {
        var media = await db.MediaFiles
            .Include(file => file.Library)
            .FirstOrDefaultAsync(file => file.Id == mediaFileId, cancellationToken);
        if (media?.Library is null)
        {
            return null;
        }

        var existing = await db.Jobs
            .Where(job => job.Type == JobType.Preview && job.MediaFileId == mediaFileId)
            .Select(job => job.Id)
            .ToListAsync(cancellationToken);
        foreach (var jobId in existing)
        {
            await DeleteAsync(jobId, cancellationToken);
        }

        var job = new Job
        {
            MediaFileId = mediaFileId,
            LibraryId = media.LibraryId,
            Type = JobType.Preview,
            Status = JobStatus.Queued,
            // High priority so a user waiting on a preview is served ahead of background work.
            Priority = int.MaxValue,
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);

        dispatcher.Wake();
        return job.Id;
    }

    /// <summary>The preview's current state, plus the comparison once it has finished.</summary>
    public async Task<PreviewComparison?> GetAsync(int jobId, CancellationToken cancellationToken)
    {
        var job = await db.Jobs
            .AsNoTracking()
            .Include(j => j.MediaFile)
            .FirstOrDefaultAsync(j => j.Id == jobId && j.Type == JobType.Preview, cancellationToken);
        if (job?.MediaFile is null)
        {
            return null;
        }

        var media = job.MediaFile;
        MediaSideStats? original = null;
        MediaSideStats? encoded = null;
        double? saving = null;
        var clipped = false;

        // Probe both sides only once the preview has finished, so polling while it runs stays cheap.
        if (job.Status is JobStatus.Completed or JobStatus.Failed)
        {
            original = await ProbeSideAsync(media.Path, cancellationToken);
            if (job.Status == JobStatus.Completed && job.WorkOutputPath is { } output && File.Exists(output))
            {
                encoded = await ProbeSideAsync(output, cancellationToken);
            }

            // A video preview only encodes a sample, so its output is materially shorter than the
            // source. Compare by bitrate in that case so the saving figure is representative.
            clipped = original?.DurationSeconds is { } od and > 0
                && encoded?.DurationSeconds is { } ed
                && ed < od - 1.0;
            saving = clipped
                ? PreviewStats.SavingPercentByRate(original?.SizeBytes, original?.DurationSeconds, encoded?.SizeBytes, encoded?.DurationSeconds)
                : PreviewStats.SavingPercent(original?.SizeBytes, encoded?.SizeBytes);
        }

        return new PreviewComparison(
            job.Id,
            media.Id,
            media.MediaKind.ToString(),
            job.Status.ToString(),
            job.Progress,
            job.ErrorMessage,
            original,
            encoded,
            saving,
            clipped,
            job.VerificationPassed,
            job.VerificationReportJson);
    }

    /// <summary>Cancels (if running), removes the preview job, and wipes its scratch output.</summary>
    public async Task<bool> DeleteAsync(int jobId, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(
            j => j.Id == jobId && j.Type == JobType.Preview, cancellationToken);
        if (job is null)
        {
            return false;
        }

        dispatcher.RequestCancel(jobId);
        db.Jobs.Remove(job);
        await db.SaveChangesAsync(cancellationToken);

        var dir = WorkOutputRoot.ForPreview(_workRoot, jobId);
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort: a file still held by a cancelling ffmpeg is reaped by the startup purge.
        }

        return true;
    }

    private async Task<MediaSideStats?> ProbeSideAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var size = new FileInfo(path).Length;
        var result = await probe.ProbeAsync(path, cancellationToken);
        if (!result.Success)
        {
            return new MediaSideStats(size, null, null, null, null, null, null, null, null);
        }

        return new MediaSideStats(
            size,
            result.Container,
            result.VideoCodec,
            result.Width,
            result.Height,
            result.DurationSeconds,
            result.MaxAudioChannels > 0 ? result.MaxAudioChannels : null,
            result.AudioCodecs.FirstOrDefault(),
            result.AudioBitrateKbps);
    }
}
