using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;
using System.Text.Json;

namespace Optimisarr.Tests;

public sealed class FrozenPerceptualEncodingTests
{
    private static readonly string Source = new('a', 64), Candidate = new('b', 64), Tool = new('c', 64), Reference = new('d', 64);
    private static RemotePerceptualSearchContract Contract() => new(Guid.NewGuid(), MediaKind.Audio, "aac", PerceptualQualityPlan.Audio("aac", 128),
        VerificationPolicy.Default with { AudioQualityGateEnabled = true, MaximumAudioQualityDistance = 0.02 },
        PerceptualSamplePlanner.AudioWindows(8), SourceAudio: new(8, 2, 48000, "stereo"));
    private static TranscodeSpec Spec() => new(WorkerProtocol.InputPlaceholder, WorkerProtocol.OutputPlaceholder + ".m4a", null, null, null, false,
        Kind: MediaKind.Audio, AudioEncoder: "aac", AudioBitrateKbps: 128);
    private static RemotePerceptualSearchState Settled(RemotePerceptualSearchContract contract)
    {
        var state = RemotePerceptualSearchState.Start(contract);
        for (var index = 0; state.AskedValue is { } asked; index++)
        {
            Assert.True(index < 4);
            var raw = new AudioQualityAssessmentResult(true, null, new(8, 2, 48000, "stereo"), new(8, 2, 48000, "stereo"),
                Reference, Candidate, Tool, Tool, Tool, [new(new(0, 8), new(384000, [0.001, asked >= 96 ? 0.01 : 0.04]))], 1);
            var report = new RemotePerceptualProbeReport(contract.Id, "aac", asked, Source, new(Audio: contract.SourceAudio),
                [new(0, asked * 10, Reference, Candidate, Audio: RemoteAudioQualityEvidence.From(raw))], EncoderSha256: Tool);
            state = RemotePerceptualSearchCoordinator.Advance(state, Source, report, new((char)('1' + index), 64)).State!;
        }
        Assert.True(state.CanEncode);
        return state;
    }

    [Fact]
    public void Selection_changes_only_the_frozen_final_setting_and_preserves_artwork_metadata_and_marker_intent()
    {
        var contract = Contract(); var spec = Spec();
        var arguments = FfmpegCommandBuilder.Build(spec, optimisedMarker: "release-at-claim").ToList();
        var frozen = FrozenPerceptualEncoding.Create(contract, spec, arguments);
        var settingIndex = arguments.IndexOf("-b:a") + 1;
        var selected = frozen.Select(Settled(contract));
        Assert.Equal(96, selected.Spec.AudioBitrateKbps);
        Assert.Equal("96k", selected.Arguments[settingIndex]);
        Assert.False(selected.Spec.AudioSampleOnly);
        var restored = selected.Arguments.ToArray(); restored[settingIndex] = "128k";
        Assert.Equal(arguments, restored);
        arguments[settingIndex] = "8k";
        Assert.Equal("128k", frozen.Arguments[settingIndex]);
    }

    [Fact]
    public void Unsettled_searches_and_another_contract_cannot_authorise_a_final_command()
    {
        var contract = Contract(); var spec = Spec();
        var frozen = FrozenPerceptualEncoding.Create(contract, spec, FfmpegCommandBuilder.Build(spec));
        Assert.Throws<InvalidOperationException>(() => frozen.Select(RemotePerceptualSearchState.Start(contract)));
        Assert.Throws<InvalidOperationException>(() => frozen.Select(Settled(Contract())));
    }

    [Fact]
    public void Ambiguous_or_changed_encode_arguments_are_refused_instead_of_rebuilt_from_current_settings()
    {
        var contract = Contract(); var spec = Spec();
        var arguments = FfmpegCommandBuilder.Build(spec).ToArray();
        foreach (var changed in new[] { arguments.Concat(["-b:a", "64k"]).ToArray(),
            arguments.Select(arg => arg == "128k" ? "64k" : arg).ToArray(),
            arguments.Select(arg => arg == "aac" ? "libopus" : arg).ToArray(),
            arguments.Select(arg => arg == spec.InputPath ? "/private/server.flac" : arg).ToArray() })
            Assert.Throws<ArgumentException>(() => FrozenPerceptualEncoding.Create(contract, spec, changed));
    }

    [Theory]
    [InlineData(".webp")]
    [InlineData(".m4a/../../outside.m4a")]
    [InlineData(".m4a?another-output")]
    public void A_frozen_encode_requires_the_exact_output_suffix_for_its_encoder(string suffix)
    {
        var spec = Spec() with { OutputPath = WorkerProtocol.OutputPlaceholder + suffix };
        Assert.Throws<ArgumentException>(() => FrozenPerceptualEncoding.Create(Contract(), spec, FfmpegCommandBuilder.Build(spec)));
    }

    [Fact]
    public void Additional_inputs_cannot_change_the_final_sources_after_a_search()
    {
        var spec = Spec(); var args = FfmpegCommandBuilder.Build(spec).ToList();
        args.InsertRange(args.Count - 1, ["-i", "/different/source.flac"]);
        Assert.Throws<ArgumentException>(() => FrozenPerceptualEncoding.Create(Contract(), spec, args));
    }

    [Fact]
    public void A_native_worker_can_independently_check_that_selection_changes_exactly_one_frozen_control()
    {
        var contract = Contract(); var args = FfmpegCommandBuilder.Build(Spec(), optimisedMarker: "frozen-release").ToArray();
        var selected = FrozenPerceptualEncoding.SelectArguments(contract, args, 96);
        var index = Array.IndexOf(args, "-b:a") + 1;
        Assert.Equal("96k", selected[index]);
        var restored = selected.ToArray(); restored[index] = "128k";
        Assert.Equal(args, restored); Assert.Equal("128k", args[index]);
        Assert.Throws<ArgumentException>(() => FrozenPerceptualEncoding.SelectArguments(contract, args, 17));
        Assert.Throws<ArgumentException>(() => FrozenPerceptualEncoding.SelectArguments(contract, args.Concat(["-b:a", "96k"]).ToArray(), 96));
        Assert.Throws<ArgumentException>(() => FrozenPerceptualEncoding.SelectArguments(contract,
            args.Select(arg => arg == "aac" ? "libopus" : arg).ToArray(), 96));
        Assert.Throws<ArgumentException>(() => FrozenPerceptualEncoding.SelectArguments(contract,
            args.Select(arg => arg == WorkerProtocol.InputPlaceholder ? "/another/source" : arg).ToArray(), 96));
    }

    [Theory]
    [InlineData("mjpeg", ".jpg")]
    [InlineData("libwebp", ".webp")]
    public void Image_setting_selection_survives_persistence_and_changes_no_other_arguments(string encoder, string suffix)
    {
        var contract = new RemotePerceptualSearchContract(Guid.NewGuid(), MediaKind.Image, encoder,
            PerceptualQualityPlan.Image(encoder, 80), VerificationPolicy.Default with
                { ImageQualityGateEnabled = false, ImageMetadataGateEnabled = false, ImagePerceptualGateEnabled = true, MinimumImagePerceptualScore = 85 },
            [], SourceImage: new(32, 32, false));
        var spec = Spec() with { Kind = MediaKind.Image, AudioEncoder = null, AudioBitrateKbps = null,
            ImageEncoder = encoder, ImageQuality = 80, OutputPath = WorkerProtocol.OutputPlaceholder + suffix };
        var args = FfmpegCommandBuilder.Build(spec).ToArray();
        var state = RemotePerceptualSearchState.Start(contract, FrozenPerceptualEncoding.Create(contract, spec, args));
        for (var index = 0; state.AskedValue is { } asked; index++)
        {
            Assert.True(index < 4);
            RemoteImagePerceptualEvidence Image(string hash, double score) => RemoteImagePerceptualEvidence.From(
                new(new(32, 32, false, 0, score, [score]), null, Source, hash, Tool, 1));
            var report = new RemotePerceptualProbeReport(contract.Id, encoder, asked, Source, new(Image: Image(Source, 100)),
                [new(0, asked * 10 + 1000, Source, Candidate, Image: Image(Candidate, asked >= 60 ? 90 : 70))], EncoderSha256: Tool);
            state = RemotePerceptualSearchCoordinator.Advance(state, Source, report, new((char)('1' + index), 64)).State!;
            state = JsonSerializer.Deserialize<RemotePerceptualSearchState>(JsonSerializer.Serialize(state))!;
        }
        var selected = state.Encoding!.Select(state);
        var indexOfSetting = Array.IndexOf(args, encoder == "mjpeg" ? "-q:v" : "-quality") + 1;
        Assert.Equal(state.Decision!.SelectedValue, selected.Spec.ImageQuality);
        var restored = selected.Arguments.ToArray(); restored[indexOfSetting] = args[indexOfSetting];
        Assert.Equal(args, restored);
        Assert.Equal(spec.OutputPath, selected.Arguments[^1]);
    }
}
