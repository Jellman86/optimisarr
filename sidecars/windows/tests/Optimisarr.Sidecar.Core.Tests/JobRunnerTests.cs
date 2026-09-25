using System.Net;
using System.Text;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// One job from claim to delivery, with a fake server and a fake encoder. What is pinned here is
/// what happens when something goes wrong part-way, because those paths are the ones that decide
/// whether a bad night costs one job or fills a disk and delivers rubbish.
/// </summary>
public sealed class JobRunnerTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "optimisarr-jobtests", Guid.NewGuid().ToString("N"));

    private static Assignment Assignment() => new(
        LeaseId: Guid.NewGuid(),
        JobId: 5888,
        Title: "The Dinosaurs - S01E01",
        SourceBytes: 13,
        VideoEncoder: "hevc_nvenc",
        Vmaf: "Cpu",
        ExpiresUtc: DateTimeOffset.UtcNow.AddMinutes(5),
        RenewWithinSeconds: 120,
        Arguments: ["-i", "{{input}}", "-c:v", "hevc_nvenc", "{{output}}.mkv"],
        OutputExtension: ".mkv",
        Quality: new QualityRequirement(false, "", 1, false, 0, 0, []));

    private static StoredPairing Pairing() => new("https://server.example.com", "secret", 7);

    [Fact]
    public async Task A_command_naming_a_file_on_this_machine_is_refused_and_the_job_handed_back()
    {
        // The contract reaching the runner, not merely existing. As LocalSystem this would have
        // read whatever it was pointed at; the source is fetched and then the command is checked,
        // so the refusal costs a download and nothing else.
        var source = Encoding.UTF8.GetBytes("source-bytes");
        var server = new FakeWorkerServer(source, Hash(source));
        var http = new HttpClient(server);
        var transcoder = new FakeMeasuringTranscoder();
        var runner = new JobRunner(
            new SidecarClient(http), new JobTransfer(http), transcoder,
            "ffmpeg.exe", _scratch, () => null);

        var assignment = Assignment() with
        {
            Arguments = ["-i", @"C:\Windows\System32\config\SAM", "-c:v:0", "hevc_nvenc", "{{output}}.mkv"],
        };

        var outcome = await runner.RunAsync(Pairing(), assignment, CancellationToken.None);

        Assert.False(outcome.Delivered);
        Assert.Contains("refused", outcome.Detail, StringComparison.OrdinalIgnoreCase);
        // Never started. An encode that ran and was then judged would already have done the damage.
        Assert.DoesNotContain(transcoder.AllRuns, run => run.Contains("hevc_nvenc"));
        Assert.False(server.Completed);
    }

    [Fact]
    public async Task A_job_is_fetched_encoded_and_delivered()
    {
        var source = Encoding.UTF8.GetBytes("source-bytes");
        var hash = Hash(source);
        var server = new FakeWorkerServer(source, hash);
        var http = new HttpClient(server);
        var assignment = Assignment();
        var candidate = Path.Combine(_scratch, $"job-{assignment.JobId}", "candidate.mkv");

        var runner = new JobRunner(
            new SidecarClient(http), new JobTransfer(http),
            new FakeMeasuringTranscoder(0, candidate), "ffmpeg.exe", _scratch, () => null);

        var outcome = await runner.RunAsync(Pairing(), assignment, CancellationToken.None);

        Assert.True(outcome.Delivered);
        Assert.True(server.Completed);
        Assert.Equal("encoded-bytes", Encoding.UTF8.GetString(server.Delivered));
        // Scratch is never left behind: a worker keeping every source it was sent fills a disk.
        Assert.False(Directory.Exists(Path.Combine(_scratch, $"job-{assignment.JobId}")));
    }

    [Fact]
    public async Task An_exceeded_size_budget_reports_a_terminal_failure_instead_of_releasing_the_job()
    {
        var source = Encoding.UTF8.GetBytes("source-bytes");
        var server = new FakeWorkerServer(source, Hash(source));
        var http = new HttpClient(server);
        var transcoder = new FakeMeasuringTranscoder { SizeBudgetExceededAtBytes = 13 };
        var assignment = Assignment() with { MaxCandidateBytes = 12 };
        var runner = new JobRunner(new SidecarClient(http), new JobTransfer(http),
            transcoder, "ffmpeg.exe", _scratch, () => null);

        var outcome = await runner.RunAsync(Pairing(), assignment, CancellationToken.None);

        Assert.False(outcome.Delivered);
        Assert.Contains("Size saving", outcome.Detail);
        Assert.Equal(12, transcoder.LastSizeBudget!.MaxBytes);
        Assert.Contains(server.Calls, call => call.EndsWith("/size-budget-exceeded", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Calls, call => call.EndsWith("/release", StringComparison.Ordinal));
        Assert.False(server.Completed);
    }

    [Fact]
    public async Task A_finished_candidate_over_budget_is_rejected_before_quality_work_or_upload()
    {
        var source = Encoding.UTF8.GetBytes("source-bytes");
        var server = new FakeWorkerServer(source, Hash(source));
        var transcoder = new FakeMeasuringTranscoder(0);
        var runner = new JobRunner(new SidecarClient(new HttpClient(server)), new JobTransfer(new HttpClient(server)),
            transcoder, "ffmpeg.exe", _scratch, () => null);

        var outcome = await runner.RunAsync(Pairing(), Assignment() with { MaxCandidateBytes = 12 }, CancellationToken.None);

        Assert.False(outcome.Delivered);
        Assert.Contains("Size saving", outcome.Detail);
        Assert.Contains(server.Calls, call => call.EndsWith("/size-budget-exceeded", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Calls, call => call.EndsWith("/release", StringComparison.Ordinal));
        Assert.False(server.Completed);
        Assert.Single(transcoder.AllRuns);
    }

    [Fact]
    public async Task A_finished_candidate_below_its_floor_is_rejected_before_quality_work_or_upload()
    {
        var source = Encoding.UTF8.GetBytes("source-bytes");
        var server = new FakeWorkerServer(source, Hash(source));
        var transcoder = new FakeMeasuringTranscoder(0);
        var runner = new JobRunner(new SidecarClient(new HttpClient(server)), new JobTransfer(new HttpClient(server)),
            transcoder, "ffmpeg.exe", _scratch, () => null);

        var outcome = await runner.RunAsync(Pairing(), Assignment() with { MinCandidateBytes = 14 }, CancellationToken.None);

        Assert.False(outcome.Delivered);
        Assert.Contains("Compression ceiling", outcome.Detail);
        Assert.Contains(server.Calls, call => call.EndsWith("/size-budget-undershot", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Calls, call => call.EndsWith("/release", StringComparison.Ordinal));
        Assert.False(server.Completed);
        Assert.Single(transcoder.AllRuns);
    }

    [Fact]
    public async Task A_source_that_did_not_arrive_intact_is_never_encoded()
    {
        // The server declares a hash that will not match what it actually sent. Encoding anyway
        // would spend an hour producing a candidate the server refuses for the wrong source.
        var server = new FakeWorkerServer(Encoding.UTF8.GetBytes("truncated"), Hash(Encoding.UTF8.GetBytes("whole")));
        var http = new HttpClient(server);
        var transcoder = new FakeMeasuringTranscoder(0, null);

        var runner = new JobRunner(
            new SidecarClient(http), new JobTransfer(http),
            transcoder, "ffmpeg.exe", _scratch, () => null);

        var outcome = await runner.RunAsync(Pairing(), Assignment(), CancellationToken.None);

        Assert.False(outcome.Delivered);
        Assert.Contains("hash mismatch", outcome.Detail);
        Assert.Null(transcoder.Arguments);   // never ran
        Assert.Contains(server.Calls, call => call.EndsWith("/release", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_encode_gives_the_job_back_with_the_reason()
    {
        var source = Encoding.UTF8.GetBytes("source-bytes");
        var server = new FakeWorkerServer(source, Hash(source));
        var http = new HttpClient(server);

        var runner = new JobRunner(
            new SidecarClient(http), new JobTransfer(http),
            new FakeMeasuringTranscoder(1, null, "Error while opening encoder for output stream"),
            "ffmpeg.exe", _scratch, () => null);

        var outcome = await runner.RunAsync(Pairing(), Assignment(), CancellationToken.None);

        Assert.False(outcome.Delivered);
        Assert.Contains("Error while opening encoder", outcome.Detail);
        // Handed back at once rather than left to lapse, so the queue can try elsewhere.
        Assert.Contains(server.Calls, call => call.EndsWith("/release", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_encode_that_claims_success_but_produces_nothing_is_not_delivered()
    {
        // Exit code zero is not proof of a file. Delivering nothing would leave the server waiting
        // on an upload that never comes.
        var source = Encoding.UTF8.GetBytes("source-bytes");
        var server = new FakeWorkerServer(source, Hash(source));
        var http = new HttpClient(server);

        var runner = new JobRunner(
            new SidecarClient(http), new JobTransfer(http),
            new FakeMeasuringTranscoder(0) { ProducesNothing = true },
            "ffmpeg.exe", _scratch, () => null);

        var outcome = await runner.RunAsync(Pairing(), Assignment(), CancellationToken.None);

        Assert.False(outcome.Delivered);
        Assert.Contains("no candidate file", outcome.Detail);
        Assert.False(server.Completed);
    }

    [Fact]
    public async Task The_server_chose_the_encode_and_only_the_paths_are_this_machines()
    {
        var source = Encoding.UTF8.GetBytes("source-bytes");
        var server = new FakeWorkerServer(source, Hash(source));
        var http = new HttpClient(server);
        var assignment = Assignment();
        var candidate = Path.Combine(_scratch, $"job-{assignment.JobId}", "candidate.mkv");
        var transcoder = new FakeMeasuringTranscoder(0, candidate);

        var runner = new JobRunner(
            new SidecarClient(http), new JobTransfer(http),
            transcoder, "ffmpeg.exe", _scratch, () => null);

        await runner.RunAsync(Pairing(), assignment, CancellationToken.None);

        // The encoder and its flags are untouched; only the two paths were filled in.
        Assert.Contains("-c:v", transcoder.Arguments!);
        Assert.Contains("hevc_nvenc", transcoder.Arguments!);
        Assert.DoesNotContain(transcoder.Arguments!, argument => argument.Contains("{{"));
    }

    [Fact]
    public async Task A_server_that_blinks_does_not_throw_away_the_encode()
    {
        // A deployment restarts the container, the proxy answers 503 for a few seconds, and every
        // encode running at that moment used to be abandoned mid-way. The lease is only really
        // gone when the server says so or when it has been out of touch for longer than the
        // window it was granted for; a refused renewal on its own says neither.
        var source = Encoding.UTF8.GetBytes("source-bytes");
        var server = new FakeWorkerServer(source, Hash(source)) { RenewStatus = HttpStatusCode.ServiceUnavailable };
        var http = new HttpClient(server);
        var assignment = Assignment() with { RenewWithinSeconds = 10 };
        var candidate = Path.Combine(_scratch, $"job-{assignment.JobId}", "candidate.mkv");

        var runner = new JobRunner(
            new SidecarClient(http), new JobTransfer(http),
            // Long enough to outlast a renewal attempt, short enough to stay inside the window.
            new FakeMeasuringTranscoder(0, candidate) { EncodeTakes = TimeSpan.FromSeconds(7) },
            "ffmpeg.exe", _scratch, () => null);

        var outcome = await runner.RunAsync(Pairing(), assignment, CancellationToken.None);

        Assert.True(server.RenewCalls > 0, "the test proves nothing if no renewal was attempted");
        Assert.True(outcome.Delivered, outcome.Detail);
        Assert.True(server.Completed);
    }

    [Fact]
    public async Task A_lease_the_server_says_is_gone_stops_the_encode_at_once()
    {
        // The other half of the same rule, and the one that must not be softened by it: a 409 is
        // the server saying this job belongs to someone else now, and finishing the encode would
        // burn a machine's evening on something nobody will accept.
        var source = Encoding.UTF8.GetBytes("source-bytes");
        var server = new FakeWorkerServer(source, Hash(source)) { RenewStatus = HttpStatusCode.Conflict };
        var http = new HttpClient(server);
        var assignment = Assignment() with { RenewWithinSeconds = 10 };
        var candidate = Path.Combine(_scratch, $"job-{assignment.JobId}", "candidate.mkv");

        var runner = new JobRunner(
            new SidecarClient(http), new JobTransfer(http),
            // Far longer than the window: if the encode is allowed to finish, this test hangs
            // around for a minute and fails on the assertion below rather than on a timeout.
            new FakeMeasuringTranscoder(0, candidate) { EncodeTakes = TimeSpan.FromSeconds(60) },
            "ffmpeg.exe", _scratch, () => null);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var outcome = await runner.RunAsync(Pairing(), assignment, CancellationToken.None);
        clock.Stop();

        Assert.False(outcome.Delivered);
        Assert.False(server.Completed);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"the encode ran on regardless: {clock.Elapsed}");
    }

    [Fact]
    public async Task A_finished_candidate_is_not_thrown_away_because_the_server_is_restarting()
    {
        // PICARD's own words, from the evening this was found:
        //
        //     Job 5960: quality evidence accepted, so the server need not measure
        //     Job 5960: delivering
        //     Job 5960: Delivering the candidate failed (HTTP 502).
        //
        // A complete encode, measured, accepted, and binned — because a deployment restarted the
        // container while the bytes were going up. Delivery is resumable; a blink should cost a
        // pause.
        var source = Encoding.UTF8.GetBytes("source-bytes");
        var server = new FakeWorkerServer(source, Hash(source)) { DeliveryFailuresRemaining = 4 };
        var http = new HttpClient(server);
        var assignment = Assignment();
        var candidate = Path.Combine(_scratch, $"job-{assignment.JobId}", "candidate.mkv");

        var runner = new JobRunner(
            new SidecarClient(http), new JobTransfer(http),
            new FakeMeasuringTranscoder(0, candidate), "ffmpeg.exe", _scratch, () => null);

        var outcome = await runner.RunAsync(Pairing(), assignment, CancellationToken.None);

        Assert.True(server.DeliveryRefusals > 0, "the test proves nothing if the server never blinked");
        Assert.True(outcome.Delivered, outcome.Detail);
        Assert.True(server.Completed);
        // Every byte, once. A resumed delivery that restarted from zero would deliver more.
        Assert.Equal("encoded-bytes", Encoding.UTF8.GetString(server.Delivered));
    }

    private static string Hash(byte[] data) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }
}
