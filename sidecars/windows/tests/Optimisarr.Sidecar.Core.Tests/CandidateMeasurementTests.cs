using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// Measuring the finished candidate, which is the one part of verification a worker may contribute.
///
/// <para>This machine did not do it at all: it searched, encoded and delivered, and the server
/// re-measured every candidate itself — on the little box the whole feature exists to spare. What
/// these pin is not only that it happens, but that it stays an <em>offer</em>: a candidate that
/// encoded perfectly well must never be handed back because a score could not be taken for it.</para>
/// </summary>
public sealed class CandidateMeasurementTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "optimisarr-measure", Guid.NewGuid().ToString("N"));

    private static readonly byte[] SourceBytes = Encoding.UTF8.GetBytes("a source of some length");

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>A probe answer with the video one frame into its container.</summary>
    private const string LeadProbe = """
        { "streams": [{ "codec_type": "video", "start_time": "0.042000" }],
          "format": { "start_time": "0.000000" } }
        """;

    private static Assignment Measured(bool measure = true, bool needsShift = true) => new(
        LeaseId: Guid.NewGuid(),
        JobId: 4242,
        Title: "The Dinosaurs - S01E01",
        SourceBytes: SourceBytes.Length,
        VideoEncoder: "hevc_nvenc",
        Vmaf: "Cpu",
        ExpiresUtc: DateTimeOffset.UtcNow.AddMinutes(5),
        RenewWithinSeconds: 120,
        Arguments: ["-i", "{{input}}", "-c:v", "hevc_nvenc", "{{output}}.mkv"],
        OutputExtension: ".mkv",
        Quality: new QualityRequirement(
            measure, "vmaf_v0.6.1", 1, false, 85, 70,
            [[
                "-nostdin", "-v", "error",
                "-i", "{{distorted}}", "-i", "{{reference}}",
                "-lavfi",
                needsShift
                    ? "[0:v]setpts=PTS-{{distortedShift}}*1000000[d];[1:v]null[r];[d][r]libvmaf=log_path={{log}}:shortest=1"
                    : "[0:v]null[d];[1:v]null[r];[d][r]libvmaf=log_path={{log}}:shortest=1",
                "-f", "null", "-",
            ]]));

    private static StoredPairing Pairing() => new("https://server.example.com", "secret", 7);

    private (FakeWorkerServer Server, JobRunner Runner, FakeMeasuringTranscoder Transcoder) Build(
        string? probeOutput = LeadProbe, bool writeLogs = true, HttpStatusCode quality = HttpStatusCode.OK)
    {
        var server = new FakeWorkerServer(SourceBytes, Sha256(SourceBytes)) { QualityStatus = quality };
        var http = new HttpClient(server);
        var transcoder = new FakeMeasuringTranscoder()
        {
            ProbeOutput = probeOutput,
            WriteVmafLogs = writeLogs,
        };
        var runner = new JobRunner(
            new SidecarClient(http), new JobTransfer(http), transcoder,
            FfmpegBesideAProbe(), _scratch, () => null);
        return (server, runner, transcoder);
    }

    /// <summary>An ffmpeg path with an ffprobe next to it, so the runner can find one.</summary>
    private string FfmpegBesideAProbe()
    {
        Directory.CreateDirectory(_scratch);
        var ffmpeg = Path.Combine(_scratch, "ffmpeg.exe");
        File.WriteAllText(ffmpeg, "");
        File.WriteAllText(Path.Combine(_scratch, "ffprobe.exe"), "");
        return ffmpeg;
    }

    [Fact]
    public async Task A_delivered_candidate_arrives_with_this_machines_measurement()
    {
        var (server, runner, _) = Build();

        var outcome = await runner.RunAsync(Pairing(), Measured(), CancellationToken.None);

        Assert.True(outcome.Delivered);
        Assert.NotNull(server.QualityBody);

        using var document = JsonDocument.Parse(server.QualityBody!);
        var body = document.RootElement;
        // Bound to both hashes, so it can only be read as evidence about these exact bytes.
        Assert.Equal(Sha256(SourceBytes), body.GetProperty("sourceSha256").GetString());
        Assert.Equal(64, body.GetProperty("candidateSha256").GetString()!.Length);
        Assert.Equal(1, body.GetProperty("logs").GetArrayLength());
        Assert.Contains("harmonic_mean", body.GetProperty("logs")[0].GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_measurement_is_offered_before_the_candidate_is_sent()
    {
        // Not a correctness requirement — the server binds evidence to the delivered hash whichever
        // order they arrive in — but it is what the other sidecar does, and a candidate uploaded
        // first would have the server start verifying before the evidence that spares it lands.
        var (server, runner, _) = Build();

        await runner.RunAsync(Pairing(), Measured(), CancellationToken.None);

        Assert.True(server.QualityOfferedBeforeDelivery);
    }

    [Fact]
    public async Task The_alignment_is_measured_against_the_files_and_substituted()
    {
        var (_, runner, transcoder) = Build();

        await runner.RunAsync(Pairing(), Measured(), CancellationToken.None);

        // Tried, not derived: the server's own measurement, cut to a few seconds, once per
        // candidate offset. A probe with a graph of its own chose offsets that were a frame wrong
        // for the measurement that followed, and failed clean encodes at harmonic 9.
        var probes = transcoder.AllRuns.Where(IsProbe).ToList();
        Assert.Equal(TimelineAlignment.FramesToTry.Count, probes.Count);
        Assert.All(probes, probe => Assert.Contains(probe, a => a.Contains("libvmaf", StringComparison.Ordinal)));

        var scoring = transcoder.AllRuns.Last(run => run.Any(a => a.Contains("libvmaf", StringComparison.Ordinal)));
        var filter = scoring.Single(a => a.Contains("libvmaf", StringComparison.Ordinal));
        // The token is gone, replaced by what the probes chose — here zero, the files being in step.
        Assert.DoesNotContain("{{distortedShift}}", filter, StringComparison.Ordinal);
        Assert.Contains("setpts=PTS-0*1000000", filter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_sampled_window_gets_its_own_alignment_probe()
    {
        var (_, runner, transcoder) = Build();
        var assignment = Measured();
        IReadOnlyList<string> Command(string seek, string trim) =>
        [
            "-nostdin", "-v", "error", "-ss", seek, "-i", "{{distorted}}",
            "-ss", seek, "-i", "{{reference}}", "-lavfi",
            $"[0:v]setpts=PTS-{{{{distortedShift}}}}*1000000,trim=start={trim}:duration=40[d];"
            + $"[1:v]trim=start={trim}:duration=40[r];[d][r]libvmaf=log_path={{{{log}}}}:shortest=1",
            "-f", "null", "-"
        ];
        assignment = assignment with { Quality = assignment.Quality with
        {
            Commands = [Command("263.01275", "4.98725"), Command("1414.996917", "5.003083")]
        } };

        await runner.RunAsync(Pairing(), assignment, CancellationToken.None);

        // Each window is probed where it will be scored: a frame lost between windows moves the
        // later one and not the earlier.
        var probes = transcoder.AllRuns.Where(IsProbe).ToList();
        Assert.Equal(2 * TimelineAlignment.FramesToTry.Count, probes.Count);
        Assert.Equal("263.01275", probes[0][Array.IndexOf(probes[0].ToArray(), "-ss") + 1]);
        Assert.Equal("1414.996917", probes[3][Array.IndexOf(probes[3].ToArray(), "-ss") + 1]);
    }

    private static bool IsProbe(IReadOnlyList<string> run)
    {
        var limit = run.ToList().LastIndexOf("-t");
        return limit >= 0 && limit + 1 < run.Count && run[limit + 1] == "5";
    }

    [Fact]
    public async Task A_command_needing_no_shift_is_not_made_to_wait_for_a_probe()
    {
        // Nothing to substitute means nothing to measure, and probing anyway would fail the whole
        // measurement on a machine whose ffprobe is missing for a command that never needed it.
        var (server, runner, transcoder) = Build(probeOutput: null);

        var outcome = await runner.RunAsync(
            Pairing(), Measured(needsShift: false), CancellationToken.None);

        Assert.True(outcome.Delivered);
        Assert.Empty(transcoder.Probes);
        Assert.NotNull(server.QualityBody);
    }

    [Fact]
    public async Task An_alignment_that_cannot_be_measured_delivers_anyway_and_offers_nothing()
    {
        // The failure mode that matters. A guessed shift would misalign the comparison it exists to
        // align, so saying nothing is right — but saying nothing must not cost the encode.
        var (server, runner, _) = Build(writeLogs: false);

        var outcome = await runner.RunAsync(Pairing(), Measured(), CancellationToken.None);

        Assert.True(outcome.Delivered);
        Assert.True(server.Completed);
        Assert.Null(server.QualityBody);
    }

    [Fact]
    public async Task A_measurement_that_will_not_run_delivers_anyway()
    {
        var (server, runner, _) = Build(writeLogs: false);

        var outcome = await runner.RunAsync(Pairing(), Measured(), CancellationToken.None);

        Assert.True(outcome.Delivered);
        Assert.Null(server.QualityBody);
    }

    [Fact]
    public async Task A_server_that_refuses_the_evidence_still_gets_its_candidate()
    {
        // The server refuses evidence it cannot bind and measures for itself. That is an ordinary
        // outcome, not a job failure, and treating it as one would hand back good encodes.
        var (server, runner, _) = Build(quality: HttpStatusCode.Conflict);

        var outcome = await runner.RunAsync(Pairing(), Measured(), CancellationToken.None);

        Assert.True(outcome.Delivered);
        Assert.True(server.Completed);
        Assert.NotNull(server.QualityBody);
    }

    [Fact]
    public async Task A_job_whose_library_measures_nothing_is_not_measured()
    {
        // A library with the gate off asks for no score, and running one would spend a second
        // encode's worth of decoding on a number nobody will read.
        var (server, runner, transcoder) = Build();

        var outcome = await runner.RunAsync(Pairing(), Measured(measure: false), CancellationToken.None);

        Assert.True(outcome.Delivered);
        Assert.Null(server.QualityBody);
        Assert.Empty(transcoder.Probes);
        Assert.DoesNotContain(transcoder.AllRuns, run => run.Any(a => a.Contains("libvmaf", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_server_that_declared_no_source_hash_is_not_measured_for()
    {
        // Evidence is bound to the source hash. Without one it could never be believed, so
        // measuring would be a second decode of the whole file for a number certain to be thrown
        // away — and on the machines this runs on that is minutes of GPU for nothing.
        var server = new FakeWorkerServer(SourceBytes, Sha256(SourceBytes)) { DeclareSourceHash = false };
        var http = new HttpClient(server);
        var transcoder = new FakeMeasuringTranscoder { ProbeOutput = LeadProbe, WriteVmafLogs = true };
        var runner = new JobRunner(
            new SidecarClient(http), new JobTransfer(http), transcoder,
            FfmpegBesideAProbe(), _scratch, () => null);

        var outcome = await runner.RunAsync(Pairing(), Measured(), CancellationToken.None);

        Assert.True(outcome.Delivered);
        Assert.Null(server.QualityBody);
        Assert.Empty(transcoder.Probes);
        Assert.DoesNotContain(transcoder.AllRuns, run => run.Any(a => a.Contains("libvmaf", StringComparison.Ordinal)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { }
    }
}
