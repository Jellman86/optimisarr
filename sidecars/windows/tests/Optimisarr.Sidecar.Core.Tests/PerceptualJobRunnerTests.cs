using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

[Collection("Native scratch ownership")]
public sealed class PerceptualJobRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "native-job-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Source = Encoding.UTF8.GetBytes("source-bytes");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private string Tool => Path.Combine(_root, "encoder");
    public PerceptualJobRunnerTests() { Directory.CreateDirectory(_root); File.WriteAllText(Tool, "test encoder"); }
    public void Dispose() => Directory.Delete(_root, true);

    private sealed class Preview : IFramePreviewExtractor
    {
        public int Calls;
        public Task<byte[]?> ExtractAsync(string ffmpeg, string source, double seconds, CancellationToken token)
        { Calls++; return Task.FromResult<byte[]?>([255, 216, 255, 217]); }
    }

    private sealed class NativeTools(string encoder) : IPerceptualJobTools
    {
        public int Probes, Preparations, Verifications;
        public bool BlockSource;
        public bool CopyMetadata = true;
        public TimeSpan MeasureDelay;
        public string? Marker;
        public string? LastSource;
        public Action<string>? Measuring;
        public bool UnavailableSample, UnavailableFinal;
        public async Task<RemotePerceptualProbeReport> MeasureAsync(RemotePerceptualSearchStep step, string source, string sourceHash, string scratch, CancellationToken token)
        {
            Probes++; LastSource = source; Measuring?.Invoke(scratch);
            if (MeasureDelay > TimeSpan.Zero) await Task.Delay(MeasureDelay, token);
            var tool = Hash(await File.ReadAllBytesAsync(encoder, token));
            var candidate = step.Value.ToString("x64");
            RemoteImagePerceptualEvidence Image(string hash, double score) => RemoteImagePerceptualEvidence.From(
                new(new(32, 32, false, 0, score, [score]), null, sourceHash, hash, tool, 1));
            if (BlockSource) return new(step.Contract.Id, step.Contract.Encoder, step.Value, sourceHash, new(Error: "unsupported original"), []);
            if (UnavailableSample) return new(step.Contract.Id, step.Contract.Encoder, step.Value, sourceHash,
                new(Image: Image(sourceHash, 100), ImageMetadata: ImageMetadataResult.Ok(new(false, false))), [], Error: "sample metric unavailable", EncoderSha256: tool);
            if (step.Contract.Kind == MediaKind.Image)
                return new(step.Contract.Id, step.Contract.Encoder, step.Value, sourceHash,
                    new(Image: Image(sourceHash, 100), ImageMetadata: ImageMetadataResult.Ok(new(false, false))),
                    [new(0, 1000 + step.Value * 10, sourceHash, candidate, Image: Image(candidate, step.Value >= 60 ? 90 : 70))], EncoderSha256: tool);
            var audio = new AudioQualityAssessmentResult(true, null, new(8, 2, 48000, "stereo"), new(8, 2, 48000, "stereo"),
                new('d', 64), candidate, tool, tool, tool, [new(new(0, 8), new(384000, [0.001, step.Value >= 96 ? 0.01 : 0.04]))], 1);
            return new(step.Contract.Id, "aac", step.Value, sourceHash, new(Audio: step.Contract.SourceAudio),
                [new(0, 1000 + step.Value * 10, new('d', 64), candidate, Audio: RemoteAudioQualityEvidence.From(audio))], EncoderSha256: tool);
        }
        public async Task<ImagePreparationResult> PrepareImageAsync(string source, string candidate, string? marker, CancellationToken token)
        {
            Preparations++; Marker = marker;
            await File.AppendAllTextAsync(candidate, "|metadata+marker", token);
            return new(CopyMetadata, true);
        }
        public async Task<RemoteVerificationEvidence> VerifyAsync(string source, string candidate, RemoteVerificationContract contract, string sourceHash, string candidateHash, CancellationToken token)
        {
            Verifications++;
            if (UnavailableFinal) return new(contract.Id, sourceHash, candidateHash, Error: "final metric unavailable");
            Assert.Equal(sourceHash, Hash(await File.ReadAllBytesAsync(source, token)));
            Assert.Equal(candidateHash, Hash(await File.ReadAllBytesAsync(candidate, token)));
            var tool = Hash(await File.ReadAllBytesAsync(encoder, token));
            if (contract.Kind == MediaKind.Image)
            {
                const string probe = """{"streams":[{"codec_type":"video","codec_name":"webp","width":32,"height":32,"pix_fmt":"rgb24"}],"format":{}}""";
                RemoteImagePerceptualEvidence Image(string hash, double score) => RemoteImagePerceptualEvidence.From(
                    new(new(32, 32, false, 0, score, [score]), null, sourceHash, hash, tool, 1));
                return new(contract.Id, sourceHash, candidateHash, probe, probe, DecodeHealthResult.Ok,
                    Image: new(Image(sourceHash, 100), Image(candidateHash, 90), SourceMetadata: ImageMetadataResult.Ok(new(false, false)),
                        CandidateMetadata: ImageMetadataResult.Ok(new(false, false)), EncoderSha256: tool, ProbeSha256: tool, MetadataToolSha256: tool));
            }
            const string audioProbe = """{"streams":[{"codec_type":"audio","codec_name":"aac","channels":2,"sample_rate":"48000","duration":"8"}],"format":{"duration":"8"}}""";
            return new(contract.Id, sourceHash, candidateHash, audioProbe, audioProbe, DecodeHealthResult.Ok,
                SourceAudio: new(true, 0, null, 8), CandidateAudio: new(true, 0, null, 8),
                AudioQuality: RemoteAudioQualityEvidence.From(new(true, null, new(8, 2, 48000, "stereo"), new(8, 2, 48000, "stereo"),
                    sourceHash, candidateHash, tool, tool, tool, [new(new(0, 8), new(384000, [0.001, 0.01]))], 1)));
        }
    }

    private sealed class Server(Assignment assignment) : HttpMessageHandler
    {
        public readonly FakeWorkerServer Transfer = new(Source, Hash(Source));
        private RemotePerceptualSearchState _state = RemotePerceptualSearchState.Start(assignment.PerceptualSearch!.Contract);
        public Action? Selected;
        public int Reports, Evidence;
        public string? ReturnedVia;
        public bool ReturnTokenWasCancelled;
        public HttpStatusCode InterruptionStatus = HttpStatusCode.NoContent;
        public bool EvidenceBeforeDelivery;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/interrupted", StringComparison.Ordinal))
            {
                ReturnedVia = "interrupted"; ReturnTokenWasCancelled = token.IsCancellationRequested;
                return new(InterruptionStatus);
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/perceptual-probe", StringComparison.Ordinal))
            {
                Reports++;
                var report = (await request.Content!.ReadFromJsonAsync<RemotePerceptualProbeReport>(SidecarClient.Json, token))!;
                var digest = Hash(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(report, SidecarClient.Json));
                var progress = RemotePerceptualSearchCoordinator.Advance(_state, Hash(Source), report, digest);
                Assert.True(progress.Accepted, progress.Error); _state = progress.State!;
                PerceptualSearchDirection direction;
                if (_state.SourceRejected) direction = new(null, null, null, true, _state.SourceError);
                else if (_state.AskedValue is { } asked)
                    direction = new(RemotePerceptualSearchPlanner.Plan(_state.Contract, asked, _state.Probes.Count,
                        new("{{input}}", "{{output}}." + assignment.OutputExtension, null, null, null, false, Kind: assignment.Kind,
                            AudioEncoder: assignment.AudioEncoder, ImageEncoder: assignment.ImageEncoder)), null, null, false, null);
                else
                {
                    Selected?.Invoke(); direction = new(null, _state.Decision!.SelectedValue,
                        FrozenPerceptualEncoding.SelectArguments(_state.Contract, assignment.Arguments, _state.Decision.SelectedValue), false, "settled");
                }
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(direction, options: SidecarClient.Json) };
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/verification", StringComparison.Ordinal))
            {
                Evidence++; EvidenceBeforeDelivery = Transfer.Delivered.Length == 0;
                var evidence = (await request.Content!.ReadFromJsonAsync<RemoteVerificationEvidence>(SidecarClient.Json, token))!;
                var objections = RemoteVerificationEvidenceValidator.Validate(assignment.FullVerification!, evidence, Hash(Source), evidence.CandidateSha256);
                return new(objections.Count == 0 ? HttpStatusCode.OK : HttpStatusCode.BadRequest);
            }
            using var inner = new HttpMessageInvoker(Transfer, disposeHandler: false);
            return await inner.SendAsync(request, token);
        }
    }

    private JobRunner Runner(Server server, NativeTools tools, FakeMeasuringTranscoder encoder, Preview? preview = null,
        long? disk = long.MaxValue, long? memory = long.MaxValue, Guid? accountedOwner = null,
        Action<bool, bool>? retirement = null) =>
        new(new(new HttpClient(server)), new(new HttpClient(server)), encoder, Tool, _root, () => null,
            wantsPreview: preview is null ? null : () => true, publishPreview: preview is null ? null : (_, _) => { }, previewExtractor: preview,
            perceptual: new(tools, _ => disk, () => memory, new PerceptualJobResourcePool()),
            accountedOwner: accountedOwner, reportNativeRetirement: retirement);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_accounted_native_run_reports_cleanup_and_keeps_a_replayable_receipt(bool image)
    {
        var assignment = PerceptualJobFixture.Assignment(image); using var server = new Server(assignment);
        var identity = Guid.NewGuid();
        var reports = new List<(bool Created, bool Retired)>();
        var outcome = await Runner(server, new(Tool), new(), accountedOwner: identity,
            retirement: (created, retired) => reports.Add((created, retired)))
            .RunAsync(new("https://server.example", "secret", 1), assignment, default);
        Assert.True(outcome.Delivered, outcome.Detail);
        Assert.Equal([(true, true)], reports);
        Assert.True(NativeScratchWorkspace.RetireAccountedJob(_root, assignment.JobId, assignment.LeaseId, identity).Retired);
        Assert.True(NativeScratchWorkspace.ForgetRetiredJob(_root, assignment.JobId, assignment.LeaseId, identity).Consumed);
    }

    [Fact]
    public async Task An_accounted_run_reports_unproved_cleanup_even_after_successful_delivery()
    {
        var assignment = PerceptualJobFixture.Assignment(); using var server = new Server(assignment);
        var identity = Guid.NewGuid();
        var tools = new NativeTools(Tool) { Measuring = path => File.AppendAllText(Path.Combine(path, NativeScratchWorkspace.MarkerName), "changed") };
        var reports = new List<(bool Created, bool Retired)>();
        var outcome = await Runner(server, tools, new(), accountedOwner: identity,
            retirement: (created, retired) => reports.Add((created, retired)))
            .RunAsync(new("https://server.example", "secret", 1), assignment, default);
        Assert.True(outcome.Delivered, outcome.Detail);
        Assert.Equal([(true, false)], reports);
        Assert.False(NativeScratchWorkspace.RetireAccountedJob(_root, assignment.JobId, assignment.LeaseId, identity).Retired);
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("cancelled")]
    [InlineData("capacity")]
    public async Task A_duplicate_nonce_cannot_report_never_started_retirement_of_an_existing_live_owner(string condition)
    {
        var assignment = PerceptualJobFixture.Assignment(); using var server = new Server(assignment);
        var identity = Guid.NewGuid();
        using var existing = NativeScratchWorkspace.CreateAccountedJob(_root, assignment.JobId, assignment.LeaseId, identity);
        using var cancelled = new CancellationTokenSource();
        if (condition == "cancelled") cancelled.Cancel();
        var reports = new List<(bool Created, bool Retired)>();
        var outcome = await Runner(server, new(Tool), new(), memory: condition == "capacity" ? 0 : long.MaxValue, accountedOwner: identity,
            retirement: (created, retired) => reports.Add((created, retired)))
            .RunAsync(new("https://server.example", "secret", 1), assignment, cancelled.Token);
        Assert.False(outcome.Delivered);
        Assert.Equal([(false, false)], reports);
        Assert.True(NativeScratchWorkspace.RetireAccountedJob(_root, assignment.JobId, assignment.LeaseId, identity).Busy);
        Assert.True(existing.Cleanup());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_parent_cancellation_returns_the_lease_neutrally_without_a_cancelled_handback_token(bool beforeFetching)
    {
        var assignment = PerceptualJobFixture.Assignment(); using var server = new Server(assignment);
        using var cancellation = new CancellationTokenSource();
        var tools = new NativeTools(Tool) { Measuring = _ => cancellation.Cancel() };
        var encoder = new FakeMeasuringTranscoder();
        if (beforeFetching) cancellation.Cancel();
        var outcome = await Runner(server, tools, encoder).RunAsync(new("https://server.example", "secret", 1), assignment, cancellation.Token);
        Assert.False(outcome.Delivered); Assert.False(outcome.CapacityBlocked);
        Assert.Equal("interrupted", server.ReturnedVia);
        Assert.False(server.ReturnTokenWasCancelled);
        Assert.Equal(beforeFetching ? 0 : 1, tools.Probes);
        Assert.Equal(0, tools.Verifications);
        Assert.Empty(encoder.AllRuns);
        Assert.DoesNotContain(Directory.GetDirectories(_root), path => Path.GetFileName(path) != NativeScratchWorkspace.ControlDirectoryName);
    }

    [Fact]
    public async Task An_unacknowledged_interruption_cannot_claim_the_server_has_released_its_lease()
    {
        var assignment = PerceptualJobFixture.Assignment(); using var server = new Server(assignment) { InterruptionStatus = HttpStatusCode.ServiceUnavailable };
        using var cancellation = new CancellationTokenSource();
        var tools = new NativeTools(Tool) { Measuring = _ => cancellation.Cancel() };
        await Assert.ThrowsAsync<HttpRequestException>(() => Runner(server, tools, new()).RunAsync(
            new("https://server.example", "secret", 1), assignment, cancellation.Token));
        Assert.Equal("interrupted", server.ReturnedVia);
        Assert.False(server.ReturnTokenWasCancelled);
        Assert.DoesNotContain(Directory.GetDirectories(_root), path => Path.GetFileName(path) != NativeScratchWorkspace.ControlDirectoryName);
    }

    [Fact]
    public async Task Native_execution_holds_a_kernel_owner_and_reclaims_completed_older_work_before_fetching()
    {
        var abandoned = NativeScratchWorkspace.CreateJob(_root, 999, Guid.NewGuid());
        var old = abandoned.DirectoryPath;
        File.WriteAllText(Path.Combine(old, "source"), "old copy");
        abandoned.Dispose();
        var assignment = PerceptualJobFixture.Assignment(); using var server = new Server(assignment);
        var tools = new NativeTools(Tool) { Measuring = _ =>
        {
            Assert.False(Directory.Exists(old));
            Assert.Equal(1, NativeScratchWorkspace.Recover(_root).Busy);
        } };
        var outcome = await Runner(server, tools, new()).RunAsync(new("https://server.example", "secret", 1), assignment, default);
        Assert.True(outcome.Delivered, outcome.Detail);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, NativeScratchWorkspace.ControlDirectoryName), "owner-*.json"));
    }

    [Fact]
    public async Task Unavailable_workspace_ownership_returns_neutral_capacity_before_media_transfer()
    {
        using var occupied = NativeScratchWorkspace.CreateCapability(_root);
        using var root = NativeWorkspaceDirectory.OpenRoot(_root, create: false);
        using var control = root.OpenDirectory(NativeScratchWorkspace.ControlDirectoryName);
        using var gate = control.OpenFile("root.lock", locked: true);
        var assignment = PerceptualJobFixture.Assignment(); using var server = new Server(assignment);
        var tools = new NativeTools(Tool); var encoder = new FakeMeasuringTranscoder();
        var outcome = await Runner(server, tools, encoder).RunAsync(new("https://server.example", "secret", 1), assignment, default);
        Assert.True(outcome.CapacityBlocked);
        Assert.Equal(0, tools.Probes); Assert.Empty(encoder.AllRuns);
        Assert.Contains(server.Transfer.Calls, call => call.EndsWith("/capacity-unavailable", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Transfer.Calls, call => call.EndsWith("/source", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Native_search_settles_before_encoding_and_independent_final_evidence_precedes_delivery(bool image)
    {
        var assignment = PerceptualJobFixture.Assignment(image); using var server = new Server(assignment);
        var tools = new NativeTools(Tool); var encoder = new FakeMeasuringTranscoder();
        var outcome = await Runner(server, tools, encoder).RunAsync(new("https://server.example", "secret", 1), assignment, default);
        Assert.True(outcome.Delivered, outcome.Detail); Assert.True(server.Transfer.Completed);
        Assert.Equal(4, tools.Probes); Assert.Equal(1, tools.Verifications); Assert.True(server.EvidenceBeforeDelivery);
        Assert.Single(encoder.AllRuns);
        Assert.Contains(image ? "62" : "96k", encoder.Arguments!);
        if (image) { Assert.Equal(1, tools.Preparations); Assert.Equal("worker-fixture", tools.Marker); }
        Assert.DoesNotContain(Directory.GetDirectories(_root), path => Path.GetFileName(path) != NativeScratchWorkspace.ControlDirectoryName);
    }

    [Fact]
    public async Task An_incomplete_native_claim_is_released_before_candidate_paths_or_tools_are_used()
    {
        var assignment = PerceptualJobFixture.Assignment() with { OutputExtension = null! };
        using var server = new Server(assignment);
        var tools = new NativeTools(Tool); var encoder = new FakeMeasuringTranscoder();
        var outcome = await Runner(server, tools, encoder).RunAsync(new("https://server.example", "secret", 1), assignment, default);
        Assert.False(outcome.Delivered); Assert.Contains("incomplete", outcome.Detail);
        Assert.Equal(0, tools.Probes); Assert.Empty(encoder.AllRuns);
        Assert.DoesNotContain(server.Transfer.Calls, call => call.EndsWith("/source", StringComparison.Ordinal));
        Assert.DoesNotContain(Directory.GetDirectories(_root), path => Path.GetFileName(path) != NativeScratchWorkspace.ControlDirectoryName);
    }

    [Fact]
    public async Task A_blocked_original_never_reaches_the_full_encoder_or_the_preview_decoder()
    {
        var assignment = PerceptualJobFixture.Assignment(); using var server = new Server(assignment);
        var tools = new NativeTools(Tool) { BlockSource = true }; var encoder = new FakeMeasuringTranscoder(); var preview = new Preview();
        var outcome = await Runner(server, tools, encoder, preview).RunAsync(new("https://server.example", "secret", 1), assignment, default);
        Assert.False(outcome.Delivered); Assert.Equal(1, server.Reports); Assert.Empty(encoder.AllRuns); Assert.Equal(0, preview.Calls);
        Assert.Equal(0, tools.Verifications); Assert.DoesNotContain(Directory.GetDirectories(_root), path => Path.GetFileName(path) != NativeScratchWorkspace.ControlDirectoryName);
    }

    [Theory]
    [InlineData(0L, 9999999999L)]
    [InlineData(9999999999L, 0L)]
    [InlineData(null, 9999999999L)]
    public async Task Unavailable_peak_resources_hold_the_job_before_fetching_or_encoding(long? disk, long? memory)
    {
        var assignment = PerceptualJobFixture.Assignment(); using var server = new Server(assignment);
        var tools = new NativeTools(Tool); var encoder = new FakeMeasuringTranscoder();
        var outcome = await Runner(server, tools, encoder, disk: disk, memory: memory).RunAsync(new("https://server.example", "secret", 1), assignment, default);
        Assert.False(outcome.Delivered); Assert.True(outcome.CapacityBlocked);
        Assert.Equal(0, tools.Probes); Assert.Empty(encoder.AllRuns);
        Assert.Contains(server.Transfer.Calls, call => call.EndsWith("/capacity-unavailable", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Transfer.Calls, call => call.EndsWith("/release", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Transfer.Calls, call => call.EndsWith("/source", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_different_encoder_binary_after_selection_cannot_reuse_the_measured_setting()
    {
        var assignment = PerceptualJobFixture.Assignment(); using var server = new Server(assignment);
        server.Selected = () => File.AppendAllText(Tool, "changed");
        var tools = new NativeTools(Tool); var encoder = new FakeMeasuringTranscoder();
        var outcome = await Runner(server, tools, encoder).RunAsync(new("https://server.example", "secret", 1), assignment, default);
        Assert.False(outcome.Delivered); Assert.Empty(encoder.AllRuns); Assert.Equal(0, tools.Verifications);
    }

    [Fact]
    public async Task Lost_lease_during_native_measurement_cancels_before_final_encoding_and_cleans_the_workspace()
    {
        var assignment = PerceptualJobFixture.Assignment(); using var server = new Server(assignment);
        server.Transfer.RenewStatus = HttpStatusCode.Conflict;
        var tools = new NativeTools(Tool) { MeasureDelay = TimeSpan.FromSeconds(30) }; var encoder = new FakeMeasuringTranscoder();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var outcome = await Runner(server, tools, encoder).RunAsync(new("https://server.example", "secret", 1), assignment, deadline.Token);
        Assert.False(outcome.Delivered); Assert.True(server.Transfer.RenewCalls > 0); Assert.Empty(encoder.AllRuns);
        Assert.DoesNotContain(Directory.GetDirectories(_root), path => Path.GetFileName(path) != NativeScratchWorkspace.ControlDirectoryName);
    }

    [Fact]
    public async Task A_changed_original_after_selection_cannot_reuse_its_measured_setting()
    {
        var assignment = PerceptualJobFixture.Assignment(); using var server = new Server(assignment);
        var tools = new NativeTools(Tool); var encoder = new FakeMeasuringTranscoder();
        server.Selected = () => File.AppendAllText(tools.LastSource!, "changed");
        var outcome = await Runner(server, tools, encoder).RunAsync(new("https://server.example", "secret", 1), assignment, default);
        Assert.False(outcome.Delivered); Assert.Empty(encoder.AllRuns); Assert.Equal(0, tools.Verifications);
    }

    [Fact]
    public async Task A_missing_transferred_source_hash_refuses_quality_first_before_native_measurement()
    {
        var assignment = PerceptualJobFixture.Assignment(); using var server = new Server(assignment);
        server.Transfer.DeclareSourceHash = false;
        var tools = new NativeTools(Tool); var encoder = new FakeMeasuringTranscoder();
        var outcome = await Runner(server, tools, encoder).RunAsync(new("https://server.example", "secret", 1), assignment, default);
        Assert.False(outcome.Delivered); Assert.Equal(0, tools.Probes); Assert.Empty(encoder.AllRuns);
    }

    [Fact]
    public async Task Metadata_and_marker_bytes_participate_in_the_final_size_gate()
    {
        var assignment = PerceptualJobFixture.Assignment() with { MaxCandidateBytes = 20 }; using var server = new Server(assignment);
        var tools = new NativeTools(Tool); var encoder = new FakeMeasuringTranscoder();
        var outcome = await Runner(server, tools, encoder).RunAsync(new("https://server.example", "secret", 1), assignment, default);
        Assert.False(outcome.Delivered); Assert.Equal(1, tools.Preparations); Assert.Equal(0, tools.Verifications);
        Assert.Contains(server.Transfer.Calls, call => call.EndsWith("/size-budget-exceeded", StringComparison.Ordinal));
        Assert.Empty(server.Transfer.Delivered);
    }

    [Fact]
    public async Task Required_image_metadata_copy_failure_holds_the_candidate_before_verification_or_delivery()
    {
        var assignment = PerceptualJobFixture.Assignment(); using var server = new Server(assignment);
        var tools = new NativeTools(Tool) { CopyMetadata = false }; var encoder = new FakeMeasuringTranscoder();
        var outcome = await Runner(server, tools, encoder).RunAsync(new("https://server.example", "secret", 1), assignment, default);
        Assert.False(outcome.Delivered); Assert.Equal(1, tools.Preparations); Assert.Equal(0, tools.Verifications);
        Assert.Empty(server.Transfer.Delivered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unavailable_sample_uses_the_saved_setting_but_still_needs_independent_final_evidence(bool unavailableFinal)
    {
        var assignment = PerceptualJobFixture.Assignment(); using var server = new Server(assignment);
        var tools = new NativeTools(Tool) { UnavailableSample = true, UnavailableFinal = unavailableFinal }; var encoder = new FakeMeasuringTranscoder();
        var outcome = await Runner(server, tools, encoder).RunAsync(new("https://server.example", "secret", 1), assignment, default);
        Assert.Equal(!unavailableFinal, outcome.Delivered); Assert.Equal(1, tools.Probes); Assert.Equal(1, tools.Verifications);
        Assert.Contains("80", encoder.Arguments!);
        if (unavailableFinal) Assert.Empty(server.Transfer.Delivered);
    }
}
