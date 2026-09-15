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
    public async Task The_candidates_extra_lead_over_the_source_is_measured_and_substituted()
    {
        var (_, runner, transcoder) = Build();

        await runner.RunAsync(Pairing(), Measured(), CancellationToken.None);

        // Both files probed, source and candidate, because the shift is the difference between them.
        Assert.Equal(2, transcoder.Probes.Count);
        var scoring = transcoder.AllRuns.Last(run => run.Any(a => a.Contains("libvmaf", StringComparison.Ordinal)));
        var filter = scoring.Single(a => a.Contains("libvmaf", StringComparison.Ordinal));
        // The token is gone, replaced by the measured difference — here zero, both files sharing a lead.
        Assert.DoesNotContain("{{distortedShift}}", filter, StringComparison.Ordinal);
        Assert.Contains("setpts=PTS-0*1000000", filter, StringComparison.Ordinal);
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
    public async Task A_lead_that_cannot_be_measured_delivers_anyway_and_offers_nothing()
    {
        // The failure mode that matters. A guessed shift would misalign the comparison it exists to
        // align, so saying nothing is right — but saying nothing must not cost the encode.
        var (server, runner, _) = Build(probeOutput: null);

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
