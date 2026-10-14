using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

public sealed class NativePerceptualProbeServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "perceptual-native-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _calls = [];
    private static readonly string SourceHash = new('a', 64), CandidateHash = new('b', 64), ToolHash = new('c', 64), ReferenceHash = new('d', 64);
    private string Source => Path.Combine(_root, "original");
    public NativePerceptualProbeServiceTests() { Directory.CreateDirectory(_root); File.WriteAllText(Source, "original"); }
    public void Dispose() => Directory.Delete(_root, true);

    private static RemotePerceptualSearchStep Step(bool image = true, string encoder = "libwebp")
    {
        var policy = VerificationPolicy.Default with { ImagePerceptualGateEnabled = true, MinimumImagePerceptualScore = 85,
            AudioQualityGateEnabled = true, MaximumAudioQualityDistance = 0.02, ImageQualityGateEnabled = true,
            AudioLoudnessGateEnabled = true, AudioClippingGateEnabled = true };
        var contract = new RemotePerceptualSearchContract(Guid.NewGuid(), image ? MediaKind.Image : MediaKind.Audio, image ? encoder : "aac",
            image ? PerceptualQualityPlan.Image(encoder, 80) : PerceptualQualityPlan.Audio("aac", 128), policy,
            image ? [] : PerceptualSamplePlanner.AudioWindows(48), SourceAudio: image ? null : new(48, 2, 48000, "stereo"),
            SourceImage: image ? new(32, 32, false) : null);
        var spec = new TranscodeSpec(WorkerProtocol.InputPlaceholder, WorkerProtocol.OutputPlaceholder + (image ? encoder == "mjpeg" ? ".jpg" : ".webp" : ".m4a"),
            null, null, null, false, Kind: contract.Kind, AudioEncoder: image ? null : "aac", AudioBitrateKbps: image ? null : 128,
            ImageEncoder: image ? encoder : null, ImageQuality: image ? 80 : null);
        return RemotePerceptualSearchPlanner.Plan(contract, contract.Plan.Baseline, 0, spec);
    }

    private PerceptualProbeOperations Operations() => new(
        Hash: (path, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(path == Source ? SourceHash
            : Path.GetFileName(path).StartsWith("reference-", StringComparison.Ordinal) ? ReferenceHash
            : Path.GetFileName(path).StartsWith("candidate-", StringComparison.Ordinal) ? CandidateHash : ToolHash); },
        Image: (source, candidate, _) =>
        {
            _calls.Add(source == candidate ? "qualify-image" : "measure-image");
            return Task.FromResult(new ImagePerceptualReport(new(32, 32, false, 0, source == candidate ? 100 : 90, [source == candidate ? 100 : 90]),
                null, SourceHash, source == candidate ? SourceHash : CandidateHash, ToolHash, 1));
        },
        Metadata: (_, _) => { _calls.Add("metadata"); return Task.FromResult(ImageMetadataResult.Ok(new(true, true))); },
        AudioInput: (_, _) => { _calls.Add("qualify-audio"); return Task.FromResult<AudioQualityInput?>(new(48, 2, 48000, "stereo")); },
        Encode: (args, path, _, token) =>
        {
            token.ThrowIfCancellationRequested(); _calls.Add("encode");
            Assert.Contains(Source, args); Assert.DoesNotContain(args, arg => arg.Contains("{{", StringComparison.Ordinal));
            Assert.Contains("-n", args); Assert.Equal(path, args[^1]);
            File.WriteAllBytes(path, new byte[1000]); return Task.FromResult<string?>(null);
        },
        CopyMetadata: (_, _, _) => { _calls.Add("copy"); return Task.FromResult(true); },
        Audio: (reference, candidate, _) =>
        {
            _calls.Add("measure-audio");
            var duration = Path.GetFileName(reference).Contains("-0.", StringComparison.Ordinal) ? 30 : 18;
            return Task.FromResult(new AudioQualityAssessmentResult(true, null, new(duration, 2, 48000, "stereo"),
                new(duration, 2, 48000, "stereo"), ReferenceHash, CandidateHash, ToolHash, ToolHash, ToolHash,
                [new(new(0, duration), new(duration * 48000, [0.01, 0.015]))], 1));
        },
        Ssim: (_, _, _, _) => { _calls.Add("ssim"); return Task.FromResult(ImageQualityResult.Ok(0.99)); },
        Loudness: (_, _) => { _calls.Add("loudness"); return Task.FromResult(LoudnessResult.Ok(-20, -5)); });

    private NativePerceptualProbeService Service(PerceptualProbeOperations? operations = null) =>
        new(new("ffmpeg", "ffprobe", "audio-metric", "image-metric", "exiftool"), operations ?? Operations());

    [Theory]
    [InlineData("libwebp")]
    [InlineData("mjpeg")]
    public async Task Image_probes_qualify_the_original_before_encoding_copy_metadata_and_return_file_bound_raw_evidence(string encoder)
    {
        var step = Step(encoder: encoder);
        var report = await Service().MeasureAsync(step, Source, SourceHash, _root, default);
        Assert.True(RemotePerceptualProbeValidator.Validate(step.Contract, 80, SourceHash, report).Accepted);
        Assert.Equal("Worker", report.Source.Image!.Assessment.MeasurementLocation);
        Assert.Equal("Worker", Assert.Single(report.Samples).Image!.Assessment.MeasurementLocation);
        Assert.Equal(["qualify-image", "metadata", "encode", "copy", "measure-image", "ssim"], _calls);
        Assert.Equal(1000, Assert.Single(report.Samples).EncodedBytes);
        Assert.Equal("original", File.ReadAllText(Source)); Assert.Empty(Directory.GetDirectories(_root));
    }

    [Theory]
    [InlineData("image")]
    [InlineData("metadata")]
    public async Task Missing_required_original_evidence_never_reaches_FFmpeg(string unavailable)
    {
        var ops = Operations();
        if (unavailable == "image") ops = ops with { Image = (_, _, _) => Task.FromResult(new ImagePerceptualReport(null, "unsupported colour", SourceHash, SourceHash, ToolHash, 0)) };
        else ops = ops with { Metadata = (_, _) => Task.FromResult(ImageMetadataResult.Failed("missing decompression support")) };
        var step = Step(); var report = await Service(ops).MeasureAsync(step, Source, SourceHash, _root, default);
        Assert.True(RemotePerceptualProbeValidator.Validate(step.Contract, 80, SourceHash, report).SourceRejected);
        Assert.DoesNotContain("encode", _calls); Assert.Empty(report.Samples); Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task Audio_probes_prepare_each_original_window_without_gain_or_channel_changes_and_preserve_all_raw_measurements()
    {
        var step = Step(image: false); var report = await Service().MeasureAsync(step, Source, SourceHash, _root, default);
        var validated = RemotePerceptualProbeValidator.Validate(step.Contract, 128, SourceHash, report);
        Assert.True(validated.Accepted, validated.Error); Assert.True(validated.Probe!.Measurement.MeetsTarget);
        Assert.Equal(2, report.Samples.Count); Assert.Equal(4, _calls.Count(call => call == "encode"));
        Assert.Equal(2, _calls.Count(call => call == "measure-audio")); Assert.Equal(4, _calls.Count(call => call == "loudness"));
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task A_later_failed_window_retains_completed_raw_evidence_and_cannot_be_a_partial_quality_pass()
    {
        var ops = Operations(); var measured = 0;
        var original = Operations().Audio;
        ops = ops with { Audio = async (reference, candidate, token) => ++measured == 1 ? await original(reference, candidate, token)
            : new(false, "metric unavailable", null, null, null, null, null, null, null, [], 0) };
        var step = Step(image: false); var report = await Service(ops).MeasureAsync(step, Source, SourceHash, _root, default);
        var validation = RemotePerceptualProbeValidator.Validate(step.Contract, 128, SourceHash, report);
        Assert.True(validation.Accepted, validation.Error); Assert.False(validation.Probe!.Measurement.Available);
        Assert.Equal(2, report.Samples.Count); Assert.True(report.Samples[0].Audio!.Assessment.Measured);
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task Commands_outside_the_canonical_sample_contract_are_refused_before_native_tools_run()
    {
        var step = Step(); step = step with { SampleCommands = [["-i", "/another/private/source", "{{output}}.webp"]] };
        await Assert.ThrowsAsync<ArgumentException>(() => Service().MeasureAsync(step, Source, SourceHash, _root, default));
        Assert.Empty(_calls); Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task Cancellation_propagates_after_the_encode_stops_and_owned_probe_scratch_is_removed()
    {
        using var cancellation = new CancellationTokenSource();
        var ops = Operations() with { Encode = (_, path, _, _) =>
        { File.WriteAllText(path, "partial"); cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(ops).MeasureAsync(Step(), Source, SourceHash, _root, cancellation.Token));
        Assert.Empty(Directory.GetDirectories(_root)); Assert.Equal("original", File.ReadAllText(Source));
    }

    [Fact]
    public async Task A_retry_with_changed_original_bytes_is_refused_before_qualification_or_sample_encoding()
    {
        var step = Step(); step = step with { Contract = step.Contract with { RequiredSourceSha256 = CandidateHash } };
        var report = await Service().MeasureAsync(step, Source, SourceHash, _root, default);
        Assert.True(RemotePerceptualProbeValidator.Validate(step.Contract, 80, SourceHash, report).SourceRejected);
        Assert.Empty(_calls); Assert.Empty(Directory.GetDirectories(_root));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("encoder")]
    public async Task A_changed_original_or_native_tool_prevents_a_completed_probe_from_authorising_a_full_encode(string changed)
    {
        var ops = Operations(); var reads = 0; var hash = ops.Hash;
        ops = ops with { Hash = (path, token) => (path == (changed == "source" ? Source : "ffmpeg") && ++reads > (changed == "source" ? 1 : 2))
            ? Task.FromResult(new string('e', 64)) : hash(path, token) };
        var step = Step(); var report = await Service(ops).MeasureAsync(step, Source, SourceHash, _root, default);
        Assert.True(RemotePerceptualProbeValidator.Validate(step.Contract, 80, SourceHash, report).SourceRejected);
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task An_unavailable_bounded_candidate_assessment_never_falls_through_to_an_ordinary_SSIM_decode()
    {
        var ops = Operations(); var measure = ops.Image;
        ops = ops with { Image = (source, candidate, token) => source == candidate ? measure(source, candidate, token)
            : Task.FromResult(new ImagePerceptualReport(null, "unsupported candidate profile", SourceHash, CandidateHash, ToolHash, 0)) };
        var step = Step(); var report = await Service(ops).MeasureAsync(step, Source, SourceHash, _root, default);
        var validation = RemotePerceptualProbeValidator.Validate(step.Contract, 80, SourceHash, report);
        Assert.True(validation.Accepted, validation.Error); Assert.False(validation.Probe!.Measurement.Available);
        Assert.DoesNotContain("ssim", _calls); Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task An_oversized_sample_is_unavailable_without_reporting_truncated_bytes_as_a_valid_measurement()
    {
        var ops = Operations() with { Encode = (_, path, maximum, _) =>
        { using var file = File.Create(path); file.SetLength(maximum + 1); return Task.FromResult<string?>(null); } };
        var step = Step(); var report = await Service(ops).MeasureAsync(step, Source, SourceHash, _root, default);
        var validation = RemotePerceptualProbeValidator.Validate(step.Contract, 80, SourceHash, report);
        Assert.True(validation.Accepted, validation.Error); Assert.False(validation.Probe!.Measurement.Available);
        Assert.Empty(report.Samples); Assert.Contains("byte budget", report.Error); Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task A_changed_metric_after_original_qualification_stops_before_any_sample_encode()
    {
        var ops = Operations(); var hash = ops.Hash;
        ops = ops with { Hash = (path, token) => path == "image-metric" ? Task.FromResult(new string('e', 64)) : hash(path, token) };
        var step = Step(); var report = await Service(ops).MeasureAsync(step, Source, SourceHash, _root, default);
        Assert.True(RemotePerceptualProbeValidator.Validate(step.Contract, 80, SourceHash, report).SourceRejected);
        Assert.DoesNotContain("encode", _calls); Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task An_unavailable_workspace_refuses_preparation_instead_of_authorising_a_baseline_encode()
    {
        var occupied = Path.Combine(_root, "occupied"); File.WriteAllText(occupied, "unrelated file");
        var step = Step(); var report = await Service().MeasureAsync(step, Source, SourceHash, occupied, default);
        Assert.True(RemotePerceptualProbeValidator.Validate(step.Contract, 80, SourceHash, report).SourceRejected);
        Assert.DoesNotContain("encode", _calls); Assert.Equal("unrelated file", File.ReadAllText(occupied));
    }
}
