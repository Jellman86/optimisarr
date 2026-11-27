using Optimisarr.Core.Workers;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// How many jobs a Windows sidecar offers to take, and the working-space admission that keeps
/// jobs running side by side from overfilling the scratch volume between them.
/// </summary>
public sealed class SidecarConcurrencyTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimisarr-concurrency", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData(" 4 ", 4)]
    public void An_unset_or_allowed_value_is_used_as_given(string? configured, int expected)
    {
        var (jobs, problem) = SidecarConcurrency.Parse(configured);

        Assert.Equal(expected, jobs);
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("5")]
    [InlineData("8")]
    [InlineData("-2")]
    [InlineData("two")]
    [InlineData("2.5")]
    public void An_unusable_value_means_one_job_and_says_why_rather_than_rounding_to_a_limit(string configured)
    {
        // "8" becoming four would be more simultaneous work than anyone chose.
        var (jobs, problem) = SidecarConcurrency.Parse(configured);

        Assert.Equal(1, jobs);
        Assert.Contains(SidecarConcurrency.Variable, problem);
        Assert.Contains(configured, problem);
    }

    [Fact]
    public void The_admission_charges_the_estimate_the_server_admitted_the_job_against()
    {
        var assignment = Assignment(sourceBytes: 10 * GiB);

        Assert.Equal(WorkerScratchBudget.RequiredBytes(10 * GiB, null, null), ConcurrentScratchAdmission.RequiredBytes(assignment));
        Assert.Equal(3 * GiB, ConcurrentScratchAdmission.RequiredBytes(assignment with { RequiredScratchBytes = 3 * GiB }));
    }

    [Fact]
    public void Two_jobs_that_each_fit_but_not_together_take_one_slot_between_them()
    {
        // 25 GiB free; each 10 GiB source needs 15 GiB of working space. The server offered both
        // against the same check-in figure, so only this machine can tell they do not fit together.
        var pool = new PerceptualJobResourcePool();
        var admission = new ConcurrentScratchAdmission(_root, _ => new(_root, 25 * GiB, null, false), pool);

        using var first = admission.TryReserve(Assignment(sourceBytes: 10 * GiB), out _);
        var second = admission.TryReserve(Assignment(sourceBytes: 10 * GiB), out _);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(10 * GiB, pool.Available(_root, 25 * GiB, null).Disk);
    }

    [Fact]
    public void Finishing_a_job_returns_its_space_to_the_next_one()
    {
        var pool = new PerceptualJobResourcePool();
        var admission = new ConcurrentScratchAdmission(_root, _ => new(_root, 25 * GiB, null, false), pool);

        admission.TryReserve(Assignment(sourceBytes: 10 * GiB), out _)!.Dispose();

        using var next = admission.TryReserve(Assignment(sourceBytes: 10 * GiB), out _);
        Assert.NotNull(next);
    }

    [Fact]
    public void Disk_backed_scratch_is_not_refused_because_memory_cannot_be_read()
    {
        // Windows reports no memory figure for a process inside a job object. That matters to RAM
        // scratch and to native measurements, not to a job working on an ordinary disk.
        var admission = new ConcurrentScratchAdmission(_root, _ => new(_root, 25 * GiB, null, false), new());

        using var reservation = admission.TryReserve(Assignment(sourceBytes: 10 * GiB), out _);
        Assert.NotNull(reservation);
    }

    [Fact]
    public void Memory_backed_scratch_is_charged_to_memory_and_refused_when_memory_is_unknown()
    {
        var pool = new PerceptualJobResourcePool();
        var known = new ConcurrentScratchAdmission(_root, _ => new(_root, 25 * GiB, 20 * GiB, true), pool);
        var unknown = new ConcurrentScratchAdmission(_root, _ => new(_root, 25 * GiB, null, true), new());

        using var reservation = known.TryReserve(Assignment(sourceBytes: 10 * GiB), out _);
        Assert.NotNull(reservation);
        Assert.Equal(5 * GiB, pool.Available(_root, 25 * GiB, 20 * GiB).Memory);
        Assert.Null(unknown.TryReserve(Assignment(sourceBytes: 10 * GiB), out _));
    }

    [Fact]
    public void Unreadable_working_space_or_an_unsized_job_is_refused()
    {
        var unreadable = new ConcurrentScratchAdmission(_root, _ => null, new());
        var readable = new ConcurrentScratchAdmission(_root, _ => new(_root, 25 * GiB, null, false), new());

        Assert.Null(unreadable.TryReserve(Assignment(sourceBytes: 1 * GiB), out _));
        Assert.Null(readable.TryReserve(Assignment(sourceBytes: 0), out _));
    }

    [Fact]
    public async Task A_job_that_no_longer_fits_is_handed_back_for_capacity_without_running()
    {
        var admission = new ConcurrentScratchAdmission(_root, _ => new(_root, 1 * GiB, null, false), new());
        var released = new List<Guid>();
        var ran = false;
        var guarded = admission.Guard(
            (_, assignment, _) => { ran = true; return Task.FromResult(new JobOutcome(assignment.JobId, true, "done")); },
            (_, lease, _) => { released.Add(lease); return Task.CompletedTask; });
        var assignment = Assignment(sourceBytes: 10 * GiB);

        var outcome = await guarded(Pairing(), assignment, CancellationToken.None);

        Assert.False(ran);
        Assert.Equal([assignment.LeaseId], released);
        Assert.True(outcome.CapacityBlocked);
        Assert.False(outcome.Delivered);
    }

    [Fact]
    public async Task A_running_job_holds_its_space_until_it_finishes()
    {
        var pool = new PerceptualJobResourcePool();
        var admission = new ConcurrentScratchAdmission(_root, _ => new(_root, 25 * GiB, null, false), pool);
        long? availableWhileRunning = null;
        var guarded = admission.Guard(
            (_, assignment, _) =>
            {
                availableWhileRunning = pool.Available(_root, 25 * GiB, null).Disk;
                return Task.FromResult(new JobOutcome(assignment.JobId, true, "done"));
            },
            (_, _, _) => throw new InvalidOperationException("Nothing should be handed back."));

        var outcome = await guarded(Pairing(), Assignment(sourceBytes: 10 * GiB), CancellationToken.None);

        Assert.True(outcome.Delivered);
        Assert.Equal(10 * GiB, availableWhileRunning);
        Assert.Equal(25 * GiB, pool.Available(_root, 25 * GiB, null).Disk);
    }

    [Fact]
    public async Task Quality_first_searches_are_passed_through_because_the_runner_reserves_their_peak()
    {
        // Charging them here too would count the same job twice and refuse work that fits.
        var admission = new ConcurrentScratchAdmission(_root, _ => null, new());
        var ran = false;
        var guarded = admission.Guard(
            (_, assignment, _) => { ran = true; return Task.FromResult(new JobOutcome(assignment.JobId, true, "done")); },
            (_, _, _) => throw new InvalidOperationException("Nothing should be handed back."));

        await guarded(Pairing(), PerceptualJobFixture.Assignment(), CancellationToken.None);

        Assert.True(ran);
    }

    [Fact]
    public void A_disk_job_is_not_refused_over_memory_other_jobs_hold()
    {
        // A quality-first job holds memory in the same pool. An ordinary disk job charges none, so
        // whether the machine's memory figure is low or unreadable must not turn it away.
        var pool = new PerceptualJobResourcePool();
        using var qualityFirst = pool.TryAcquire(_root, 1 * GiB, 4 * GiB, 25 * GiB, 8 * GiB);
        Assert.NotNull(qualityFirst);
        var lowMemory = new ConcurrentScratchAdmission(_root, _ => new(_root, 25 * GiB, 1 * GiB, false), pool);
        var unreadableMemory = new ConcurrentScratchAdmission(_root, _ => new(_root, 25 * GiB, null, false), pool);

        using var first = lowMemory.TryReserve(Assignment(sourceBytes: 4 * GiB), out var firstRefusal);
        using var second = unreadableMemory.TryReserve(Assignment(sourceBytes: 4 * GiB), out var secondRefusal);

        Assert.NotNull(first);
        Assert.Null(firstRefusal);
        Assert.NotNull(second);
        Assert.Null(secondRefusal);
    }

    [Fact]
    public void Each_refusal_names_its_actual_cause()
    {
        var pool = new PerceptualJobResourcePool();
        var readable = new ConcurrentScratchAdmission(_root, _ => new(_root, 25 * GiB, null, false), pool);
        var unreadable = new ConcurrentScratchAdmission(_root, _ => null, new());
        var small = new ConcurrentScratchAdmission(_root, _ => new(_root, 1 * GiB, null, false), new());

        Assert.Null(readable.TryReserve(Assignment(sourceBytes: 0), out var unsized));
        Assert.Null(unreadable.TryReserve(Assignment(sourceBytes: 1 * GiB), out var unread));
        Assert.Null(small.TryReserve(Assignment(sourceBytes: 10 * GiB), out var tooSmall));
        using var running = readable.TryReserve(Assignment(sourceBytes: 10 * GiB), out _);
        Assert.Null(readable.TryReserve(Assignment(sourceBytes: 10 * GiB), out var crowded));

        Assert.Equal(ConcurrentScratchAdmission.SizeUnknown, unsized);
        Assert.Equal(ConcurrentScratchAdmission.SpaceUnreadable, unread);
        Assert.Equal(ConcurrentScratchAdmission.TooLittleSpace, tooSmall);
        Assert.Equal(ConcurrentScratchAdmission.OtherJobsHoldSpace, crowded);
        Assert.Equal(4, new[] { unsized, unread, tooSmall, crowded }.Distinct().Count());
    }

    [Fact]
    public async Task A_handed_back_job_reports_why()
    {
        var admission = new ConcurrentScratchAdmission(_root, _ => null, new());
        var guarded = admission.Guard(
            (_, assignment, _) => Task.FromResult(new JobOutcome(assignment.JobId, true, "done")),
            (_, _, _) => Task.CompletedTask);

        var outcome = await guarded(Pairing(), Assignment(sourceBytes: 1 * GiB), CancellationToken.None);

        Assert.Equal(ConcurrentScratchAdmission.SpaceUnreadable, outcome.Detail);
    }

    [Fact]
    public async Task One_job_at_a_time_runs_the_job_as_given()
    {
        // The path every Windows worker has run so far: no extra hold, so nothing it accepted
        // before can now be handed back.
        Func<StoredPairing, Assignment, CancellationToken, Task<JobOutcome>> run =
            (_, assignment, _) => Task.FromResult(new JobOutcome(assignment.JobId, true, "done"));

        var chosen = ConcurrentScratchAdmission.ForConcurrency(1, _root, run,
            (_, _, _) => throw new InvalidOperationException("Nothing should be handed back."), _ => null, new());

        Assert.Same(run, chosen);
        Assert.True((await chosen(Pairing(), Assignment(sourceBytes: 1 * GiB), CancellationToken.None)).Delivered);
    }

    [Fact]
    public async Task More_than_one_job_at_a_time_holds_each_jobs_working_space()
    {
        var released = 0;
        var ran = false;
        var chosen = ConcurrentScratchAdmission.ForConcurrency(2, _root,
            (_, assignment, _) => { ran = true; return Task.FromResult(new JobOutcome(assignment.JobId, true, "done")); },
            (_, _, _) => { released++; return Task.CompletedTask; }, _ => null, new());

        var outcome = await chosen(Pairing(), Assignment(sourceBytes: 1 * GiB), CancellationToken.None);

        Assert.False(ran);
        Assert.Equal(1, released);
        Assert.True(outcome.CapacityBlocked);
    }

    private static Assignment Assignment(long sourceBytes) => new(
        LeaseId: Guid.NewGuid(),
        JobId: 41,
        Title: "Concurrent",
        SourceBytes: sourceBytes,
        VideoEncoder: "hevc_amf",
        Vmaf: "Cpu",
        ExpiresUtc: DateTimeOffset.UtcNow.AddMinutes(5),
        RenewWithinSeconds: 120,
        Arguments: ["-i", "{{input}}", "-c:v", "hevc_amf", "{{output}}.mkv"],
        OutputExtension: ".mkv",
        Quality: new QualityRequirement(false, "", 1, false, 0, 0, []));

    private static StoredPairing Pairing() => new("https://server.example.com", "secret", 7);
}
