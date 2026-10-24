using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Deduplication;
using Optimisarr.Api.Library;
using Optimisarr.Api.Replacement;
using Optimisarr.Core.Library;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Data;

namespace Optimisarr.Api.Queue;

public sealed record PreparedWorkerSample(string Path, long Bytes, MediaProbeResult Probe, double PresentationOffset);

public interface IWorkerSampleSource
{
    Task<PreparedWorkerSample> PrepareAsync(Job job, int seconds, int start, CancellationToken token);
}

/// <summary>Copies a bounded, immutable source segment; workers verify its complete picture sequence.</summary>
internal sealed class WorkerSampleSource(
    IServiceScopeFactory scopes, FileOperationClaims claims, IHostEnvironment environment,
    VerificationService verification, IMediaProbeService probe, ReferenceFrameAlignmentProbe alignment) : IWorkerSampleSource
{
    public static PreparedWorkerSample? FromLease(Job job, JobLease lease, string workRoot)
    {
        if (job.Type == JobType.Normal || lease.VerificationWorkJson is null) return null;
        try
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            options.Converters.Add(new JsonStringEnumConverter());
            var work = JsonSerializer.Deserialize<QueueDispatcher.JobWork>(lease.VerificationWorkJson, options);
            var sample = work.WorkerSample;
            if (!work.IsDisposable || sample is null || sample.Path != work.Original.Path
                || Path.GetDirectoryName(Path.GetFullPath(sample.Path)) != Path.GetFullPath(WorkerJobPaths.Root(workRoot, job))
                || !Path.GetFileName(sample.Path).StartsWith(".worker-source-", StringComparison.Ordinal)
                || !LibraryPathSafety.IsSafeFile(sample.Path, workRoot)) return null;
            return sample;
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return null; }
    }

    private readonly SemaphoreSlim _preparation = new(1, 1);

    public async Task<PreparedWorkerSample> PrepareAsync(Job job, int seconds, int start, CancellationToken token)
    {
        if (job.Type == JobType.Normal || seconds is <= 0 or > 60 || start < 0 || job.MediaFile?.MediaKind != MediaKind.Video)
            throw new InvalidOperationException("Only bounded interactive samples may use a prepared worker source.");
        await _preparation.WaitAsync(token);
        try
        {
            var source = job.MediaFile.Path;
            if (!LibraryPathSafety.IsSafeFile(source, job.MediaFile.Library?.Path))
                throw new InvalidOperationException(LibraryPathSafety.Refusal);
            var root = WorkerJobPaths.Root(WorkPaths.Resolve(environment), job);
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            IDisposable held;
            await using (var write = await db.BeginDiagnosticWriteAsync(token))
            {
                held = await FileMutationGuard.TryAcquireAsync(db, claims, job.MediaFileId, [source, root], token)
                    ?? throw new InvalidOperationException("Sample preparation is waiting for another file operation.");
                await write.CommitAsync(token);
            }
            using (held)
            await using (var sourceGuard = FileContentIdentity.OpenAttemptGuard(source, "The sample source is unavailable."))
            {
                // A disposable sample can never replace the original, so a cheap identity is enough to
                // name it and to notice a change mid-cut. Hashing a 50 GB original twice was not (#428).
                var identity = SampleIdentity(source);
                var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
                var extension = Path.GetExtension(source).ToLowerInvariant();
                if (extension.Length is < 2 or > 12 || extension.Skip(1).Any(c => !char.IsAsciiLetterOrDigit(c)))
                    throw new InvalidOperationException("The source has no safe media extension for a bounded sample.");
                var workRoot = WorkPaths.Resolve(environment);
                Directory.CreateDirectory(workRoot);
                if (!LibraryPathSafety.IsSafeToPruneMissingFile(Path.Combine(root, ".worker-source-pending"), workRoot))
                    throw new InvalidOperationException("The sample work directory contains an unsafe path component.");
                Directory.CreateDirectory(root);
                if (!LibraryPathSafety.IsSafeDirectory(root))
                    throw new InvalidOperationException("The sample work directory is not a regular directory.");
                var path = Path.Combine(root, $".worker-source-{hash}-{start}-{seconds}{extension}");
                if (!File.Exists(path))
                {
                    var temporary = Path.Combine(root, $".worker-source-{Guid.NewGuid():N}{extension}");
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromMinutes(2));
                    try
                    {
                        var arguments = PreviewReferenceClipCommandBuilder.Build(source, temporary, seconds, start);
                        var run = await verification.RunReferenceClipAsync(["-nostdin", "-xerror", .. arguments], timeout.Token);
                        if (run.ExitCode != 0)
                            throw new InvalidOperationException($"Could not prepare the worker's source sample: {run.Error}");
                        if (SampleIdentity(source) != identity)
                            throw new InvalidOperationException("The source changed during sample preparation.");
                        File.Move(temporary, path);
                    }
                    finally
                    {
                        if (File.Exists(temporary)) File.Delete(temporary);
                    }
                }
                if (!LibraryPathSafety.IsSafeFile(path, root))
                    throw new InvalidOperationException("The prepared sample path is no longer safe.");
                var measured = await probe.ProbeAsync(path, token);
                var duration = MediaTimelineDuration.Resolve(job.MediaFile.MediaKind, measured.VideoDurationSeconds, measured.DurationSeconds);
                if (!measured.Success || duration is not > 0 || duration > seconds + 60
                    || job.MediaFile.MediaKind == MediaKind.Video && new FileInfo(path).Length > sourceGuard.Length + 1024 * 1024)
                    throw new InvalidOperationException("The source could not produce a bounded, readable worker sample.");
                var offset = job.Type == JobType.Calibration && job.MediaFile.MediaKind == MediaKind.Video
                    ? await alignment.MeasureAsync(source, start, token)
                        ?? throw new InvalidOperationException("The worker sample could not be aligned to the requested scene.")
                    : 0;
                return new(path, new FileInfo(path).Length, measured, offset);
            }
        }
        finally { _preparation.Release(); }
    }

    private static string SampleIdentity(string path)
    {
        var file = new FileInfo(path);
        return $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
    }
}

internal static class WorkerJobPaths
{
    public static string Root(string workRoot, Job job) => job.Type switch
    {
        JobType.Preview => WorkOutputRoot.ForPreview(workRoot, job.Id),
        JobType.Calibration when job.CalibrationSessionId is { } session => WorkOutputRoot.ForCalibration(workRoot, session, job.Id),
        JobType.Normal => WorkOutputRoot.ForMediaFile(workRoot, job.MediaFileId),
        _ => throw new InvalidOperationException("A calibration job must belong to a session.")
    };
}
