using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Deduplication;
using Optimisarr.Api.Library;
using Optimisarr.Api.Queue;
using Optimisarr.Data;

namespace Optimisarr.Api.Endpoints;

internal static partial class WorkerResultEndpoints
{
    internal const int UploadBufferBytes = 1024 * 1024;

    internal sealed record UploadSnapshot(Guid LeaseId, int JobId, int MediaFileId, int Attempt,
        int LeaseAttempt, int WorkerId, string? SourceHash, string SourcePath, string? WorkOutputPath,
        string? Extension, string StagingPath, string FinalPath)
    {
        public static UploadSnapshot Capture(IHostEnvironment environment, Job job, JobLease lease) =>
            new(lease.Id, job.Id, job.MediaFileId, job.ExecutionAttempt, lease.ExecutionAttempt, lease.WorkerId,
                job.SourceSha256, job.MediaFile!.Path, job.WorkOutputPath, lease.OutputExtension,
                StagingPathFor(environment, job, lease), FinalPathFor(environment, job, lease));

        public string Binding => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { Contract = 1, LeaseId, JobId, MediaFileId, Attempt, LeaseAttempt, WorkerId, SourceHash, SourcePath, WorkOutputPath, Extension, StagingPath, FinalPath }))).ToLowerInvariant();

        public bool Matches(Job job, JobLease lease) => job.Id == JobId && job.MediaFileId == MediaFileId
            && job.ExecutionAttempt == Attempt && lease.ExecutionAttempt == LeaseAttempt && lease.WorkerId == WorkerId
            && job.SourceSha256 == SourceHash && job.MediaFile!.Path == SourcePath
            && job.WorkOutputPath == WorkOutputPath && lease.OutputExtension == Extension;

        public bool Owns(string path) => path == StagingPath || (path.StartsWith(FinalPath + ".", StringComparison.Ordinal)
            && path.EndsWith(".partial", StringComparison.Ordinal) && path.Length == FinalPath.Length + 41
            && Guid.TryParseExact(path[(FinalPath.Length + 1)..^8], "N", out _));
    }

    internal sealed record UploadState(Guid Generation, string Path, long Bytes);
    internal sealed record UploadStarted(IResult? Refusal, UploadState? Upload);
    private sealed record UploadStreamed(IResult? Refusal, long Bytes, string? Hash);

    private static IResult UploadChanged() => ApiErrors.Conflict("worker.result.uploadChanged",
        "This upload changed or is sealed for completion. Query its offset or retry completion before sending more bytes.");

    private static IResult OffsetMismatch(long expected, long actual) => Results.Json(
        new ApiError("worker.result.offsetMismatch",
            $"The staged upload holds {actual} bytes, not {expected}; resume from {actual}.", new { bytes = actual }),
        statusCode: StatusCodes.Status409Conflict);

    private static void ClearUpload(JobLease lease)
    {
        lease.ResultUploadGeneration = null;
        lease.ResultUploadBinding = null;
        lease.ResultUploadPath = null;
        lease.ResultUploadSealedBytes = null;
    }

    // The network and full hash are deliberately outside this fence. Closing the file inside the
    // callback also keeps buffered writes from escaping the SQLite writer and shared claims.
    private static async Task<IResult?> WithUploadWriteAsync(OptimisarrDbContext db, FileOperationClaims claims,
        UploadSnapshot snapshot, HttpRequest? http, SettingsStore? settings, string? path,
        Func<JobLease, Task<IResult?>> mutate, CancellationToken token)
    {
        await using var write = await db.Database.BeginTransactionAsync(token);
        db.ChangeTracker.Clear();
        var persisted = await db.JobLeases.AsNoTracking().Where(l => l.Id == snapshot.LeaseId)
            .Select(l => new { l.ResultUploadPath, Source = l.Job!.MediaFile!.Path, l.Job.WorkOutputPath })
            .SingleOrDefaultAsync(token);
        using var held = await FileMutationGuard.TryAcquireAsync(db, claims, snapshot.MediaFileId,
            new[] { path, snapshot.StagingPath, snapshot.FinalPath, snapshot.SourcePath, snapshot.WorkOutputPath,
                persisted?.ResultUploadPath, persisted?.Source, persisted?.WorkOutputPath }.OfType<string>().ToArray(), token);
        if (held is null) return ApiErrors.Conflict("dedupe.reserved",
            "This file is reserved by another operation. Resolve or finish that operation before delivering it.");
        JobLease lease;
        if (http is not null)
        {
            var current = await ResolveHeldLeaseAsync(snapshot.LeaseId, http, settings!, db, token, recordSeen: false);
            if (current.Refusal is { } refusal) return refusal;
            if (!snapshot.Matches(current.Job!, current.Lease!))
                return ApiErrors.Conflict("worker.result.attemptEnded", "This assignment changed during delivery; the candidate was not accepted.");
            lease = current.Lease!;
        }
        else
        {
            var current = await db.JobLeases.SingleOrDefaultAsync(l => l.Id == snapshot.LeaseId, token);
            if (current is null) return UploadChanged();
            lease = current;
        }
        var result = await mutate(lease);
        if (result is not null) return result;
        await db.SaveChangesAsync(token);
        await write.CommitAsync(token);
        return null;
    }

    internal static async Task<UploadStarted> StartUploadAsync(OptimisarrDbContext db, FileOperationClaims claims,
        UploadSnapshot snapshot, HttpRequest http, SettingsStore settings, bool singleShot, long offset, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Extension)) return new(ApiErrors.Conflict("worker.result.contractMissing",
            "That lease records no output container, so its candidate cannot be named safely."), null);
        if (string.IsNullOrWhiteSpace(snapshot.SourceHash)) return new(ApiErrors.Conflict("worker.result.evidenceMissing",
            "Fetch this assignment's source before uploading its candidate."), null);
        UploadState? upload = null;
        var path = singleShot ? snapshot.FinalPath + $".{Guid.NewGuid():N}.partial" : snapshot.StagingPath;
        var refusal = await WithUploadWriteAsync(db, claims, snapshot, http, settings, path, async lease =>
        {
            if (lease.ResultUploadSealedBytes is not null) return UploadChanged();
            if (lease.ResultUploadPath is { } previous && !snapshot.Owns(previous)) return UploadChanged();
            if (!singleShot && lease.ResultUploadGeneration is { } generation)
            {
                if (lease.ResultUploadBinding != snapshot.Binding) return UploadChanged();
                if (lease.ResultUploadPath is not { } existing || !File.Exists(existing)) return UploadChanged();
                var bytes = StagedBytes(existing);
                if (bytes != offset) return OffsetMismatch(offset, bytes);
                upload = new(generation, existing, bytes);
                return null;
            }
            var staged = singleShot ? 0 : StagedBytes(path);
            if (staged != offset) return OffsetMismatch(offset, staged);
            if (singleShot && lease.ResultUploadPath is { } old && File.Exists(old)) File.Delete(old);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Legacy resumable staging is adopted without truncation. New single-shot names must
            // never overwrite a prior candidate, including after a retry or filesystem collision.
            await using (var file = new FileStream(path, singleShot ? FileMode.CreateNew : FileMode.OpenOrCreate,
                FileAccess.Write, FileShare.None, 1, useAsync: true)) { }
            lease.ResultUploadGeneration = Guid.NewGuid();
            lease.ResultUploadBinding = snapshot.Binding;
            lease.ResultUploadPath = path;
            lease.ResultUploadSealedBytes = null;
            upload = new(lease.ResultUploadGeneration.Value, path, staged);
            return null;
        }, token);
        return new(refusal, upload);
    }

    internal static Task<IResult?> AppendUploadBufferAsync(OptimisarrDbContext db, FileOperationClaims claims,
        UploadSnapshot snapshot, UploadState upload, HttpRequest http, SettingsStore settings,
        ReadOnlyMemory<byte> buffer, long offset, CancellationToken token) =>
        WithUploadWriteAsync(db, claims, snapshot, http, settings, upload.Path, async lease =>
        {
            if (lease.ResultUploadGeneration != upload.Generation || lease.ResultUploadPath != upload.Path
                || lease.ResultUploadBinding != snapshot.Binding || lease.ResultUploadSealedBytes is not null) return UploadChanged();
            if (buffer.Length > UploadBufferBytes) throw new ArgumentOutOfRangeException(nameof(buffer));
            if (!File.Exists(upload.Path)) return UploadChanged();
            await using var file = new FileStream(upload.Path, FileMode.Open, FileAccess.Write, FileShare.None, 1, useAsync: true);
            if (file.Length != offset) return OffsetMismatch(offset, file.Length);
            file.Position = offset;
            await file.WriteAsync(buffer, token);
            await file.FlushAsync(token);
            return null;
        }, token);

    private static async Task<UploadStreamed> StreamUploadAsync(OptimisarrDbContext db, FileOperationClaims claims,
        UploadSnapshot snapshot, UploadState upload, HttpRequest http, SettingsStore settings, CancellationToken token)
    {
        var buffer = new byte[UploadBufferBytes];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var offset = upload.Bytes;
        int read;
        while ((read = await http.Body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, token)) > 0)
        {
            var refusal = await AppendUploadBufferAsync(db, claims, snapshot, upload, http, settings,
                buffer.AsMemory(0, read), offset, token);
            if (refusal is not null) return new(refusal, offset, null);
            hash.AppendData(buffer.AsSpan(0, read));
            offset += read;
            // SQLite busy retries can otherwise let a fast local sender repeatedly reacquire the
            // writer. The measured 1 ms gap gives control-plane writers a turn outside all claims.
            await Task.Delay(1, token);
        }
        return new(null, offset, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    internal static async Task<UploadStarted> SealUploadAsync(OptimisarrDbContext db, FileOperationClaims claims,
        UploadSnapshot snapshot, HttpRequest http, SettingsStore settings, UploadState? expected, long? bytes, CancellationToken token)
    {
        UploadState? upload = null;
        var refusal = await WithUploadWriteAsync(db, claims, snapshot, http, settings, expected?.Path, lease =>
        {
            var path = lease.ResultUploadPath ?? snapshot.StagingPath;
            if (!snapshot.Owns(path)) return Task.FromResult<IResult?>(UploadChanged());
            if (lease.ResultUploadGeneration is not null && lease.ResultUploadBinding != snapshot.Binding)
                return Task.FromResult<IResult?>(UploadChanged());
            if (expected is not null && (lease.ResultUploadGeneration != expected.Generation || path != expected.Path))
                return Task.FromResult<IResult?>(UploadChanged());
            var staged = File.Exists(path) ? StagedBytes(path)
                : lease.ResultUploadSealedBytes is not null && File.Exists(snapshot.FinalPath) ? StagedBytes(snapshot.FinalPath) : -1;
            if (staged < 0) return Task.FromResult<IResult?>(ApiErrors.Conflict("worker.result.nothingStaged",
                "No chunks have been uploaded for this lease, so there is nothing to complete."));
            if (bytes is not null && staged != bytes || lease.ResultUploadSealedBytes is { } sealedBytes && sealedBytes != staged)
                return Task.FromResult<IResult?>(UploadChanged());
            lease.ResultUploadGeneration ??= Guid.NewGuid();
            lease.ResultUploadBinding = snapshot.Binding;
            lease.ResultUploadPath = path;
            lease.ResultUploadSealedBytes = staged;
            upload = new(lease.ResultUploadGeneration.Value, path, staged);
            return Task.FromResult<IResult?>(null);
        }, token);
        return new(refusal, upload);
    }

    private static async Task CleanupInterruptedUploadAsync(OptimisarrDbContext db, FileOperationClaims claims,
        UploadSnapshot snapshot, UploadState upload)
    {
        // A disconnected request has an already-cancelled token. Cleanup gets a separate bounded
        // attempt; failure preserves bytes and ownership for a later offset query/retry.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await WithUploadWriteAsync(db, claims, snapshot, null, null, upload.Path, lease =>
            {
                if (lease.ResultUploadGeneration != upload.Generation || lease.ResultUploadPath != upload.Path
                    || lease.ResultUploadSealedBytes is not null) return Task.FromResult<IResult?>(UploadChanged());
                File.Delete(upload.Path);
                ClearUpload(lease);
                return Task.FromResult<IResult?>(null);
            }, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DbException or OperationCanceledException)
        {
            // The original transfer failure remains authoritative; retained staging is resumable.
        }
    }
}
