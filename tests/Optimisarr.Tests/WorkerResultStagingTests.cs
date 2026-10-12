using System.Data.Common;
using Microsoft.AspNetCore.Http;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Optimisarr.Api.Endpoints;
using Optimisarr.Api.Deduplication;
using Optimisarr.Api.Library;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed partial class WorkerResultUploadTests
{
    private async Task<WorkerResultEndpoints.UploadSnapshot> Snapshot(string leaseId)
    {
        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var lease = await db.JobLeases.AsNoTracking().Include(l => l.Job).ThenInclude(j => j!.MediaFile)
            .SingleAsync(l => l.Id == Guid.Parse(leaseId));
        return WorkerResultEndpoints.UploadSnapshot.Capture(_api.Services.GetRequiredService<IHostEnvironment>(), lease.Job!, lease);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("path")]
    [InlineData("attempt")]
    [InlineData("reassigned")]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("extension")]
    public async Task A_changed_assignment_during_a_chunk_read_cannot_append(string change)
    {
        await EnableRemoteWorkers();
        using var worker = await PairWorker("Fresh chunk assignment");
        await QueueAJob();
        var (leaseId, sourceHash) = await ClaimAndFetch(worker);
        var (final, staging) = await DeliveryPathsAsync(leaseId);
        (await worker.SendAsync(Chunk(leaseId, CandidateBytes[..5], 0))).EnsureSuccessStatusCode();
        using var scope = _api.Services.CreateScope();
        var context = UploadContext(scope.ServiceProvider, worker, leaseId, sourceHash, true,
            new ReviewCallbackStream(CandidateBytes[5..], async () =>
            {
                using var update = _api.Services.CreateScope();
                var db = update.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
                var lease = await db.JobLeases.Include(l => l.Job).ThenInclude(j => j!.MediaFile).SingleAsync(l => l.Id == Guid.Parse(leaseId));
                switch (change)
                {
                    case "source": lease.Job!.SourceSha256 = new('f', 64); break;
                    case "path": lease.Job!.MediaFile!.Path += ".changed"; break;
                    case "attempt": lease.Job!.ExecutionAttempt++; break;
                    case "reassigned": lease.WorkerId = (await db.Workers.FirstAsync(w => w.Id != lease.WorkerId)).Id; break;
                    case "expired": lease.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); break;
                    case "revoked": (await db.Workers.SingleAsync(w => w.Id == lease.WorkerId)).CredentialFingerprint = null; break;
                    case "extension": lease.OutputExtension = ".mkv"; break;
                }
                await db.SaveChangesAsync();
            }), 5);
        await UploadEndpoint(true).RequestDelegate!(context);
        Assert.Equal(change == "revoked" ? 401 : change == "reassigned" ? 403 : 409, context.Response.StatusCode);
        Assert.Equal(CandidateBytes[..5], await File.ReadAllBytesAsync(staging));
        Assert.False(File.Exists(final));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_persisted_completion_seal_blocks_an_independent_append_and_survives_retry(bool promotedBeforeCrash)
    {
        await EnableRemoteWorkers();
        using var worker = await PairWorker("Sealed completion");
        await QueueAJob();
        var (leaseId, sourceHash) = await ClaimAndFetch(worker);
        var (final, staging) = await DeliveryPathsAsync(leaseId);
        (await worker.SendAsync(Chunk(leaseId, CandidateBytes, 0))).EnsureSuccessStatusCode();
        var snapshot = await Snapshot(leaseId);
        WorkerResultEndpoints.UploadState upload;
        using (var first = _api.Services.CreateScope())
        {
            var db = first.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var http = UploadContext(first.ServiceProvider, worker, leaseId, sourceHash, true, Stream.Null).Request;
            var seal = await WorkerResultEndpoints.SealUploadAsync(db, new(), snapshot, http,
                first.ServiceProvider.GetRequiredService<SettingsStore>(), null, null, default);
            Assert.Null(seal.Refusal);
            upload = seal.Upload!;
        }
        using (var second = _api.Services.CreateScope())
        {
            var http = UploadContext(second.ServiceProvider, worker, leaseId, sourceHash, true, Stream.Null).Request;
            var refused = await WorkerResultEndpoints.AppendUploadBufferAsync(second.ServiceProvider.GetRequiredService<OptimisarrDbContext>(),
                new(), snapshot, upload, http, second.ServiceProvider.GetRequiredService<SettingsStore>(), new byte[] { 99 }, CandidateBytes.Length, default);
            Assert.NotNull(refused);
            Assert.Equal(CandidateBytes, await File.ReadAllBytesAsync(staging));
        }
        if (promotedBeforeCrash) File.Move(staging, final);
        using var completed = await worker.SendAsync(Complete(leaseId, sourceHash, Sha256(CandidateBytes)));
        Assert.Equal(System.Net.HttpStatusCode.Accepted, completed.StatusCode);
        Assert.Equal(CandidateBytes, await File.ReadAllBytesAsync(final));
    }

    [Fact]
    public async Task An_independent_completion_during_a_body_read_freezes_the_prefix_before_hashing()
    {
        await EnableRemoteWorkers();
        using var worker = await PairWorker("Seal during read");
        await QueueAJob();
        var (leaseId, sourceHash) = await ClaimAndFetch(worker);
        var (_, staging) = await DeliveryPathsAsync(leaseId);
        (await worker.SendAsync(Chunk(leaseId, CandidateBytes[..5], 0))).EnsureSuccessStatusCode();
        var snapshot = await Snapshot(leaseId);
        using var scope = _api.Services.CreateScope();
        var context = UploadContext(scope.ServiceProvider, worker, leaseId, sourceHash, true,
            new ReviewCallbackStream(CandidateBytes[5..], async () =>
            {
                using var other = _api.Services.CreateScope();
                var request = UploadContext(other.ServiceProvider, worker, leaseId, sourceHash, true, Stream.Null).Request;
                var seal = await WorkerResultEndpoints.SealUploadAsync(other.ServiceProvider.GetRequiredService<OptimisarrDbContext>(),
                    new(), snapshot, request, other.ServiceProvider.GetRequiredService<SettingsStore>(), null, null, default);
                Assert.Null(seal.Refusal);
            }), 5);
        await UploadEndpoint(true).RequestDelegate!(context);
        Assert.Equal(409, context.Response.StatusCode);
        Assert.Equal(CandidateBytes[..5], await File.ReadAllBytesAsync(staging));
        using var complete = await worker.SendAsync(Complete(leaseId, sourceHash, Sha256(CandidateBytes[..5])));
        Assert.Equal(System.Net.HttpStatusCode.Accepted, complete.StatusCode);
    }

    [Fact]
    public async Task Independent_coordinators_cannot_append_the_same_offset_twice()
    {
        await EnableRemoteWorkers();
        using var worker = await PairWorker("Independent append");
        await QueueAJob();
        var (leaseId, sourceHash) = await ClaimAndFetch(worker);
        var (_, staging) = await DeliveryPathsAsync(leaseId);
        (await worker.SendAsync(Chunk(leaseId, [], 0))).EnsureSuccessStatusCode();
        var snapshot = await Snapshot(leaseId);
        using var first = _api.Services.CreateScope(); using var second = _api.Services.CreateScope();
        async Task<IResult?> Append(IServiceProvider services)
        {
            var db = services.GetRequiredService<OptimisarrDbContext>();
            var row = await db.JobLeases.AsNoTracking().SingleAsync(l => l.Id == Guid.Parse(leaseId));
            var upload = new WorkerResultEndpoints.UploadState(row.ResultUploadGeneration!.Value, staging, 0);
            var request = UploadContext(services, worker, leaseId, sourceHash, true, Stream.Null).Request;
            return await WorkerResultEndpoints.AppendUploadBufferAsync(db, new(), snapshot, upload, request,
                services.GetRequiredService<SettingsStore>(), CandidateBytes, 0, default);
        }
        var results = await Task.WhenAll(Task.Run(() => Append(first.ServiceProvider)), Task.Run(() => Append(second.ServiceProvider)));
        Assert.Single(results, r => r is null); Assert.Single(results, r => r is not null);
        Assert.Equal(CandidateBytes, await File.ReadAllBytesAsync(staging));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_single_shot_cleanup_is_fenced_and_preserves_reserved_staging(bool reserve)
    {
        await EnableRemoteWorkers();
        using var worker = await PairWorker("Interrupted cleanup");
        await QueueAJob();
        var (leaseId, sourceHash) = await ClaimAndFetch(worker);
        var (final, _) = await DeliveryPathsAsync(leaseId);
        var actionId = Guid.NewGuid();
        using var scope = _api.Services.CreateScope();
        var context = UploadContext(scope.ServiceProvider, worker, leaseId, sourceHash, false,
            new ReviewCallbackStream(CandidateBytes, async () =>
            {
                if (reserve)
                {
                    using var update = _api.Services.CreateScope();
                    var db = update.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
                    await ReserveUpload(db, actionId, final);
                }
                throw new IOException("interrupted network body");
            }));
        try
        {
            await Assert.ThrowsAsync<IOException>(() => UploadEndpoint(false).RequestDelegate!(context));
            using var check = _api.Services.CreateScope();
            var lease = await check.ServiceProvider.GetRequiredService<OptimisarrDbContext>().JobLeases.AsNoTracking().SingleAsync(l => l.Id == Guid.Parse(leaseId));
            Assert.Equal(reserve, lease.ResultUploadGeneration is not null);
            if (reserve) Assert.Empty(await File.ReadAllBytesAsync(lease.ResultUploadPath!));
            Assert.Null(lease.ResultUploadSealedBytes);
            Assert.False(File.Exists(final));
        }
        finally { await ReleaseUploadReservation(actionId); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upload_and_reservation_writers_serialize_before_any_staging_append(bool reservationFirst)
    {
        await EnableRemoteWorkers();
        using var worker = await PairWorker("Writer ordering");
        await QueueAJob();
        var (leaseId, sourceHash) = await ClaimAndFetch(worker);
        var (final, staging) = await DeliveryPathsAsync(leaseId);
        (await worker.SendAsync(Chunk(leaseId, [], 0))).EnsureSuccessStatusCode();
        var snapshot = await Snapshot(leaseId);
        using var scope = _api.Services.CreateScope();
        var sourceDb = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var row = await sourceDb.JobLeases.AsNoTracking().SingleAsync(l => l.Id == Guid.Parse(leaseId));
        var upload = new WorkerResultEndpoints.UploadState(row.ResultUploadGeneration!.Value, staging, 0);
        var pause = new UploadReservationPause();
        await using var uploadDb = new OptimisarrDbContext(new DbContextOptionsBuilder<OptimisarrDbContext>()
            .UseSqlite(sourceDb.Database.GetConnectionString()).AddInterceptors(pause).Options);
        var request = UploadContext(scope.ServiceProvider, worker, leaseId, sourceHash, true, Stream.Null).Request;
        var actionId = Guid.NewGuid();
        Task<IResult?> Append() => Task.Run(() => WorkerResultEndpoints.AppendUploadBufferAsync(uploadDb, new(), snapshot, upload,
            request, scope.ServiceProvider.GetRequiredService<SettingsStore>(), CandidateBytes, 0, default));
        try
        {
            if (reservationFirst)
            {
                await using var write = await sourceDb.Database.BeginTransactionAsync();
                await ReserveUpload(sourceDb, actionId, final);
                var append = Append(); await Task.Delay(100);
                Assert.False(append.IsCompleted); Assert.Empty(await File.ReadAllBytesAsync(staging));
                await write.CommitAsync();
                Assert.NotNull(await append.WaitAsync(TimeSpan.FromSeconds(20)));
                Assert.Empty(await File.ReadAllBytesAsync(staging));
            }
            else
            {
                pause.Armed = true;
                var append = Append(); await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var reserve = Task.Run(async () =>
                {
                    using var other = _api.Services.CreateScope();
                    var db = other.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
                    await using var write = await db.Database.BeginTransactionAsync();
                    await ReserveUpload(db, actionId, final); await write.CommitAsync();
                });
                try { await Task.Delay(100); Assert.False(reserve.IsCompleted); Assert.Empty(await File.ReadAllBytesAsync(staging)); }
                finally { pause.Continue.TrySetResult(); }
                Assert.Null(await append.WaitAsync(TimeSpan.FromSeconds(20)));
                await reserve.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal(CandidateBytes, await File.ReadAllBytesAsync(staging));
            }
        }
        finally { pause.Continue.TrySetResult(); await ReleaseUploadReservation(actionId); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_new_request_cannot_adopt_staging_bound_to_a_different_source(bool complete)
    {
        await EnableRemoteWorkers();
        using var worker = await PairWorker("Persisted source binding");
        await QueueAJob();
        var (leaseId, _) = await ClaimAndFetch(worker);
        var (_, staging) = await DeliveryPathsAsync(leaseId);
        (await worker.SendAsync(Chunk(leaseId, CandidateBytes, 0))).EnsureSuccessStatusCode();
        using (var change = _api.Services.CreateScope())
        {
            var db = change.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var lease = await db.JobLeases.Include(l => l.Job).SingleAsync(l => l.Id == Guid.Parse(leaseId));
            lease.Job!.SourceSha256 = new('e', 64); await db.SaveChangesAsync();
        }
        using var response = await worker.SendAsync(complete ? Complete(leaseId, new('e', 64), Sha256(CandidateBytes))
            : Chunk(leaseId, new byte[] { 99 }, CandidateBytes.Length));
        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(CandidateBytes, await File.ReadAllBytesAsync(staging));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completion_preserves_staging_when_sealed_length_changes_or_another_final_exists(bool differentFinal)
    {
        await EnableRemoteWorkers();
        using var worker = await PairWorker("Stable completion evidence");
        await QueueAJob();
        var (leaseId, sourceHash) = await ClaimAndFetch(worker);
        var (final, staging) = await DeliveryPathsAsync(leaseId);
        (await worker.SendAsync(Chunk(leaseId, CandidateBytes, 0))).EnsureSuccessStatusCode();
        var snapshot = await Snapshot(leaseId);
        using var scope = _api.Services.CreateScope();
        var request = UploadContext(scope.ServiceProvider, worker, leaseId, sourceHash, true, Stream.Null).Request;
        var seal = await WorkerResultEndpoints.SealUploadAsync(scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>(), new(),
            snapshot, request, scope.ServiceProvider.GetRequiredService<SettingsStore>(), null, null, default);
        Assert.Null(seal.Refusal);
        if (differentFinal) await File.WriteAllTextAsync(final, "different final bytes");
        else await File.AppendAllTextAsync(staging, "changed length");
        using var response = await worker.SendAsync(Complete(leaseId, sourceHash, Sha256(CandidateBytes)));
        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(File.Exists(staging));
        if (differentFinal) Assert.Equal("different final bytes", await File.ReadAllTextAsync(final));
        else Assert.False(File.Exists(final));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reservation_between_bounded_reads_preserves_the_written_prefix(bool resumable)
    {
        await EnableRemoteWorkers();
        using var worker = await PairWorker("Bounded transfer");
        await QueueAJob();
        var (leaseId, sourceHash) = await ClaimAndFetch(worker);
        var (final, staging) = await DeliveryPathsAsync(leaseId);
        var bytes = new byte[WorkerResultEndpoints.UploadBufferBytes + 3]; Array.Fill(bytes, (byte)23);
        var id = Guid.NewGuid();
        using var scope = _api.Services.CreateScope();
        var context = UploadContext(scope.ServiceProvider, worker, leaseId, sourceHash, resumable,
            new AfterPrefixStream(bytes, WorkerResultEndpoints.UploadBufferBytes, async () =>
            {
                using var other = _api.Services.CreateScope();
                await ReserveUpload(other.ServiceProvider.GetRequiredService<OptimisarrDbContext>(), id, final);
            }));
        try
        {
            await UploadEndpoint(resumable).RequestDelegate!(context);
            Assert.Equal(409, context.Response.StatusCode);
            var path = resumable ? staging : Assert.Single(Directory.GetFiles(Path.GetDirectoryName(final)!, Path.GetFileName(final) + ".*.partial"));
            Assert.Equal(bytes[..WorkerResultEndpoints.UploadBufferBytes], await File.ReadAllBytesAsync(path));
            Assert.False(File.Exists(final));
        }
        finally { await ReleaseUploadReservation(id); }
    }

    [Fact]
    public async Task Interrupted_resumable_upload_reports_the_real_prefix_and_continues_after_restart()
    {
        await EnableRemoteWorkers();
        using var worker = await PairWorker("Interrupted resume");
        await QueueAJob();
        var (leaseId, sourceHash) = await ClaimAndFetch(worker);
        var (_, staging) = await DeliveryPathsAsync(leaseId);
        var bytes = new byte[WorkerResultEndpoints.UploadBufferBytes + 3]; Array.Fill(bytes, (byte)51);
        using (var scope = _api.Services.CreateScope())
        {
            var context = UploadContext(scope.ServiceProvider, worker, leaseId, sourceHash, true,
                new AfterPrefixStream(bytes, WorkerResultEndpoints.UploadBufferBytes, () => throw new IOException("connection dropped")));
            await Assert.ThrowsAsync<IOException>(() => UploadEndpoint(true).RequestDelegate!(context));
        }
        Assert.Equal(WorkerResultEndpoints.UploadBufferBytes, new FileInfo(staging).Length);
        using var offset = await worker.GetAsync($"/api/workers/leases/{leaseId}/result/offset");
        var json = await offset.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(WorkerResultEndpoints.UploadBufferBytes, json.GetProperty("bytes").GetInt64());
        (await worker.SendAsync(Chunk(leaseId, bytes[^3..], WorkerResultEndpoints.UploadBufferBytes))).EnsureSuccessStatusCode();
        using var complete = await worker.SendAsync(Complete(leaseId, sourceHash, Sha256(bytes)));
        Assert.Equal(System.Net.HttpStatusCode.Accepted, complete.StatusCode);
    }

    private sealed class AfterPrefixStream(byte[] bytes, int prefix, Func<Task> callback) : MemoryStream(bytes)
    {
        private bool called;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= prefix && !called) { called = true; await callback(); }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    [Fact]
    public async Task A_lease_from_an_older_attempt_cannot_start_a_fresh_upload()
    {
        await EnableRemoteWorkers();
        using var worker = await PairWorker("Old attempt");
        await QueueAJob();
        var (leaseId, sourceHash) = await ClaimAndFetch(worker);
        var (final, _) = await DeliveryPathsAsync(leaseId);
        using (var scope = _api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var lease = await db.JobLeases.Include(l => l.Job).SingleAsync(l => l.Id == Guid.Parse(leaseId));
            lease.Job!.ExecutionAttempt++; await db.SaveChangesAsync();
        }
        using var response = await worker.SendAsync(Upload(leaseId, CandidateBytes, sourceHash));
        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(File.Exists(final));
    }

    [Fact]
    public async Task A_reassigned_holder_cannot_adopt_the_previous_workers_staging()
    {
        await EnableRemoteWorkers();
        using var worker = await PairWorker("Original upload holder");
        using var otherWorker = await PairWorker("Reassigned upload holder");
        await QueueAJob();
        var (leaseId, _) = await ClaimAndFetch(worker);
        var (_, staging) = await DeliveryPathsAsync(leaseId);
        (await worker.SendAsync(Chunk(leaseId, CandidateBytes, 0))).EnsureSuccessStatusCode();
        using (var scope = _api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var lease = await db.JobLeases.SingleAsync(l => l.Id == Guid.Parse(leaseId));
            var credential = otherWorker.DefaultRequestHeaders.Authorization!.Parameter!;
            var fingerprint = WorkerCredential.Fingerprint(credential);
            lease.WorkerId = (await db.Workers.SingleAsync(w => w.CredentialFingerprint == fingerprint)).Id;
            await db.SaveChangesAsync();
        }
        using var response = await otherWorker.SendAsync(Chunk(leaseId, new byte[] { 99 }, CandidateBytes.Length));
        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(CandidateBytes, await File.ReadAllBytesAsync(staging));
    }

    private static async Task ReserveUpload(OptimisarrDbContext db, Guid id, string path)
    {
        db.DedupeActions.Add(new() { Id = id, ReviewKey = new('a', 64), ScopeKey = new('b', 64), PlanHash = new('c', 64) });
        db.DedupeFileClaims.Add(new() { ActionId = id,
            Key = Assert.Single(FileOperationClaims.Keys(int.MaxValue, path), k => k.StartsWith("path:")) });
        await db.SaveChangesAsync();
    }

    private async Task ReleaseUploadReservation(Guid id)
    {
        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await db.DedupeFileClaims.Where(c => c.ActionId == id).ExecuteDeleteAsync();
        await db.DedupeActions.Where(a => a.Id == id).ExecuteDeleteAsync();
    }

    private sealed class UploadReservationPause : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("DedupeFileClaims", StringComparison.Ordinal))
            {
                Armed = false; Entered.TrySetResult(); await Continue.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
