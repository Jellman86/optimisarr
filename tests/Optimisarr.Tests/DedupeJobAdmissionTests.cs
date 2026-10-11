using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Api.Deduplication;
using Optimisarr.Api.Library;
using Optimisarr.Core.Deduplication;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Workers;
using Optimisarr.Data;
using System.Runtime.InteropServices;

namespace Optimisarr.Tests;

public sealed class DedupeJobAdmissionTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimisarr-admission-" + Guid.NewGuid());
    private ServiceProvider _services = null!;
    private DedupeJournalStore Store => _services.GetRequiredService<DedupeJournalStore>();
    private DedupePreparation Plan => new(Guid.NewGuid(), 1, new('a', 64), new('b', 64),
    [
        new(1, 1, _root, Path.Combine(_root, "keeper.mkv"), null, "disk:1", 42, 1, new('c', 64), true),
        new(2, 1, _root, Path.Combine(_root, "copy.mkv"), Path.Combine(_root, "quarantine", "copy"), "disk:2", 42, 1, new('c', 64), false)
    ]);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var services = new ServiceCollection();
        services.AddDbContext<OptimisarrDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(_root, "journal.db")};Pooling=False"));
        services.AddSingleton<FileOperationClaims>();
        services.AddSingleton<IFileReservationKeys>(sp => sp.GetRequiredService<FileOperationClaims>());
        services.AddSingleton<DedupeJournalStore>();
        _services = services.BuildServiceProvider();
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await db.Database.MigrateAsync();
        db.Libraries.Add(new() { Id = 1, Name = "Fixtures", Path = _root });
        db.MediaFiles.AddRange(Plan.Files.Select(f => new MediaFile { Id = f.MediaFileId, LibraryId = 1,
            Path = f.Path, RelativePath = Path.GetFileName(f.Path), Status = MediaFileStatus.Probed,
            MediaKind = MediaKind.Video, VideoCodec = "h264", Height = 1080, SizeBytes = 1_000_000_000 }));
        db.MediaFiles.Add(new() { Id = 3, LibraryId = 1, Path = Path.Combine(_root, "other.mkv") });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() { await _services.DisposeAsync(); Directory.Delete(_root, true); }

    [Theory]
    [InlineData(JobType.Normal)]
    [InlineData(JobType.Preview)]
    [InlineData(JobType.Calibration)]
    public async Task A_reserved_source_cannot_admit_any_job_type(JobType type)
    {
        Assert.Equal(DedupePreparationResult.Created, await Store.TryPrepareAsync(Plan, default));
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        db.Jobs.Add(new() { MediaFileId = 1, Type = type });
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Equal(0, await db.Jobs.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task A_reservation_blocks_retry_from_a_stale_context_but_allows_terminal_diagnostics()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var job = new Job { MediaFileId = 1, Status = JobStatus.Failed };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        Assert.Equal(DedupePreparationResult.Created, await Store.TryPrepareAsync(Plan, default));
        job.ErrorMessage = "Retained diagnostic";
        await db.SaveChangesAsync();
        job.Status = JobStatus.Queued;
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        await using var check = _services.CreateAsyncScope();
        var persisted = await check.ServiceProvider.GetRequiredService<OptimisarrDbContext>().Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(JobStatus.Failed, persisted.Status);
        Assert.Equal("Retained diagnostic", persisted.ErrorMessage);
    }

    [Fact]
    public async Task A_reserved_destination_cannot_become_an_unrelated_jobs_output()
    {
        var plan = Plan;
        await Store.TryPrepareAsync(plan, default);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        db.Jobs.Add(new() { MediaFileId = 3, WorkOutputPath = plan.Files[1].QuarantinePath });
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_held_worker_lease_cannot_bypass_the_job_guard(bool trackedJob)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var job = new Job { MediaFileId = 1, Status = JobStatus.Failed };
        var worker = new Worker { Name = "Fixture", CredentialFingerprint = new('e', 64) };
        db.AddRange(job, worker);
        await db.SaveChangesAsync();
        var plan = Plan;
        await Store.TryPrepareAsync(plan, default);
        if (!trackedJob) db.ChangeTracker.Clear();
        db.JobLeases.Add(new() { Id = Guid.NewGuid(), JobId = job.Id, WorkerId = worker.Id, State = LeaseState.Held });
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Empty(await db.JobLeases.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Candidates_explain_the_reservation_and_cancellation_makes_them_available_again()
    {
        var plan = Plan;
        await Store.TryPrepareAsync(plan, default);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var candidates = new CandidateService(db);
        var decision = await candidates.EvaluateFileAsync(1, default);
        Assert.False(decision!.IsEligible);
        Assert.Contains("duplicate", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.All(await candidates.EvaluateAsync(1, default), c => Assert.False(c.Eligible));
        await Store.CancelPreparedAsync(plan.Id, default);
        Assert.True((await candidates.EvaluateFileAsync(1, default))!.IsEligible);
    }

    [Fact]
    public async Task Preparation_cannot_reserve_an_active_jobs_output_even_when_its_source_is_unrelated()
    {
        var plan = Plan;
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        db.Jobs.Add(new() { MediaFileId = 3, WorkOutputPath = plan.Files[1].QuarantinePath });
        await db.SaveChangesAsync();
        Assert.Equal(DedupePreparationResult.Conflict, await Store.TryPrepareAsync(plan, default));
        Assert.Empty(await db.DedupeActions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Separate_contexts_racing_admission_and_preparation_never_commit_both()
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var plan = Plan;
            plan = plan with { Files = [plan.Files[0], plan.Files[1] with { QuarantinePath = Path.Combine(_root, "quarantine", $"race-{attempt}") }] };
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var admission = Task.Run(async () =>
            {
                await using var scope = _services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
                db.Jobs.Add(new() { MediaFileId = 1, Type = (JobType)(attempt % 3) });
                await start.Task;
                try { await db.SaveChangesAsync(); return true; }
                catch (DedupeReservationConflictException) { return false; }
            });
            var preparation = Task.Run(async () => { await start.Task; return await Store.TryPrepareAsync(plan, default); });
            start.SetResult();
            await Task.WhenAll(admission, preparation);
            var admitted = await admission;
            var prepared = await preparation;
            Assert.NotEqual(admitted, prepared == DedupePreparationResult.Created);
            await using var check = _services.CreateAsyncScope();
            var context = check.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            Assert.Equal(admitted ? 1 : 0, await context.Jobs.CountAsync());
            Assert.Equal(admitted ? 0 : 7, await context.DedupeFileClaims.CountAsync());
            if (prepared == DedupePreparationResult.Created) await Store.CancelPreparedAsync(plan.Id, default);
            await context.Jobs.ExecuteDeleteAsync();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_physical_alias_is_protected_whichever_operation_commits_first(bool preparationFirst)
    {
        var plan = Plan;
        await File.WriteAllTextAsync(plan.Files[0].Path, "keeper bytes");
        var alias = Path.Combine(_root, "other.mkv");
        Assert.True(OperatingSystem.IsWindows() ? CreateHardLink(alias, plan.Files[0].Path, IntPtr.Zero) : Link(plan.Files[0].Path, alias) == 0);
        plan = plan with { Files = [plan.Files[0] with { Identity = ExactFileIdentity.Read(plan.Files[0].Path)! }, plan.Files[1]] };
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        if (preparationFirst)
        {
            Assert.Equal(DedupePreparationResult.Created, await Store.TryPrepareAsync(plan, default));
            db.Jobs.Add(new() { MediaFileId = 3 });
            await Assert.ThrowsAsync<DedupeReservationConflictException>(() => db.SaveChangesAsync());
        }
        else
        {
            db.Jobs.Add(new() { MediaFileId = 3 });
            await db.SaveChangesAsync();
            Assert.Equal(DedupePreparationResult.Conflict, await Store.TryPrepareAsync(plan, default));
        }
        Assert.Equal("keeper bytes", await File.ReadAllTextAsync(alias));
    }

    [Theory]
    [InlineData(ReplacementStatus.Pending)]
    [InlineData(ReplacementStatus.RollbackPending)]
    [InlineData(ReplacementStatus.Replaced)]
    public async Task Preparation_cannot_claim_another_inventory_ids_retained_rollback_path(ReplacementStatus status)
    {
        var plan = Plan;
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var job = new Job { MediaFileId = 3, Status = JobStatus.Completed };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        db.Replacements.Add(new() { JobId = job.Id, MediaFileId = 3, Status = status,
            OriginalPath = Path.Combine(_root, "old"), QuarantinePath = plan.Files[0].Path, FinalPath = Path.Combine(_root, "final") });
        await db.SaveChangesAsync();
        Assert.Equal(DedupePreparationResult.Conflict, await Store.TryPrepareAsync(plan, default));
    }

    [Fact]
    public async Task A_stale_job_source_cannot_hide_its_current_persisted_reservation()
    {
        await using var staleScope = _services.CreateAsyncScope();
        var stale = staleScope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var job = new Job { MediaFileId = 3, Status = JobStatus.Failed };
        stale.Jobs.Add(job);
        await stale.SaveChangesAsync();
        await using (var newerScope = _services.CreateAsyncScope())
        {
            var newer = newerScope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var moved = await newer.Jobs.SingleAsync(j => j.Id == job.Id);
            moved.MediaFileId = 1;
            await newer.SaveChangesAsync();
        }
        await Store.TryPrepareAsync(Plan, default);
        job.Status = JobStatus.Queued;
        await Assert.ThrowsAsync<DedupeReservationConflictException>(() => stale.SaveChangesAsync());
    }

    [Fact]
    public async Task A_held_lease_protects_its_source_even_if_the_job_status_is_terminal()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var job = new Job { MediaFileId = 1, Status = JobStatus.Failed };
        var worker = new Worker { Name = "Fixture", CredentialFingerprint = new('e', 64) };
        db.AddRange(job, worker);
        await db.SaveChangesAsync();
        db.JobLeases.Add(new() { Id = Guid.NewGuid(), JobId = job.Id, WorkerId = worker.Id });
        await db.SaveChangesAsync();
        Assert.Equal(DedupePreparationResult.Conflict, await Store.TryPrepareAsync(Plan, default));
    }

    [Fact]
    public async Task New_inventory_and_job_batches_for_unreserved_files_remain_valid()
    {
        await Store.TryPrepareAsync(Plan, default);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        db.Jobs.AddRange(Enumerable.Range(0, 2).Select(i => new Job { MediaFile = new MediaFile {
            LibraryId = 1, Path = Path.Combine(_root, $"new-{i}.mkv") } }));
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.Jobs.CountAsync());
    }

    [Fact]
    public async Task Pending_inventory_path_changes_cannot_hide_a_reserved_source_from_job_admission()
    {
        var plan = Plan;
        await Store.TryPrepareAsync(plan, default);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var media = await db.MediaFiles.SingleAsync(f => f.Id == 3);
        media.Path = plan.Files[1].QuarantinePath!;
        db.Jobs.Add(new() { MediaFileId = 3 });
        await Assert.ThrowsAsync<DedupeReservationConflictException>(() => db.SaveChangesAsync());
        await using var check = _services.CreateAsyncScope();
        var persisted = await check.ServiceProvider.GetRequiredService<OptimisarrDbContext>().MediaFiles.AsNoTracking().SingleAsync(f => f.Id == 3);
        Assert.Equal(Path.Combine(_root, "other.mkv"), persisted.Path);
    }

    [Theory]
    [InlineData(JobStatus.Probing)]
    [InlineData(JobStatus.Leased)]
    [InlineData(JobStatus.Verifying)]
    public async Task A_stale_local_or_remote_claim_racing_preparation_has_one_committed_winner(JobStatus status)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var job = new Job { MediaFileId = 1, Status = JobStatus.Failed };
        var worker = new Worker { Name = "Claim fixture", CredentialFingerprint = new('e', 64) };
        db.AddRange(job, worker);
        await db.SaveChangesAsync();
        var plan = Plan;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claim = Task.Run(async () =>
        {
            job.Status = status;
            if (status == JobStatus.Leased)
                db.JobLeases.Add(new() { Id = Guid.NewGuid(), JobId = job.Id, WorkerId = worker.Id });
            await start.Task;
            try { await db.SaveChangesAsync(); return true; }
            catch (DedupeReservationConflictException) { return false; }
        });
        var preparation = Task.Run(async () => { await start.Task; return await Store.TryPrepareAsync(plan, default); });
        start.SetResult();
        await Task.WhenAll(claim, preparation);
        var claimed = await claim;
        Assert.NotEqual(claimed, await preparation == DedupePreparationResult.Created);
        await using var check = _services.CreateAsyncScope();
        var context = check.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        Assert.Equal(claimed ? status : JobStatus.Failed, (await context.Jobs.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(claimed && status == JobStatus.Leased ? 1 : 0, await context.JobLeases.CountAsync());
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string source, string target);
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string target, string source, IntPtr reserved);
}
