using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

public sealed class RemotePerceptualSearchPlannerTests
{
    [Fact]
    public void Audio_step_uses_original_source_windows_actual_bitrate_and_owned_non_overwriting_outputs()
    {
        var audio = new AudioQualityInput(95.5, 2, 44100, "stereo");
        var contract = new RemotePerceptualSearchContract(Guid.NewGuid(), MediaKind.Audio, "aac", PerceptualQualityPlan.Audio("aac", 128),
            VerificationPolicy.Default with { AudioQualityGateEnabled = true, MaximumAudioQualityDistance = 0.02 },
            PerceptualSamplePlanner.AudioWindows(audio.DurationSeconds), SourceAudio: audio);
        var spec = new TranscodeSpec("/private/source.flac", "/private/output.m4a", null, null, null, false,
            Kind: MediaKind.Audio, AudioEncoder: "aac", AudioBitrateKbps: 128);
        var step = RemotePerceptualSearchPlanner.Plan(contract, 96, 2, spec);
        Assert.Equal(contract.Id, step.Contract.Id);
        Assert.Equal(96, step.Value);
        Assert.Equal(2, step.CandidateIndex);
        Assert.Equal(3, step.SampleCommands.Count);
        Assert.Equal(3, step.ReferenceCommands.Count);
        foreach (var args in step.SampleCommands)
        {
            Assert.Contains(WorkerProtocol.InputPlaceholder, args);
            Assert.Equal(WorkerProtocol.OutputPlaceholder + ".m4a", args[^1]);
            Assert.Contains("96k", args);
            Assert.Contains("-n", args);
            Assert.DoesNotContain("-y", args);
            Assert.DoesNotContain("0:s?", args);
            Assert.Contains((RemotePerceptualProbeValidator.MaximumSampleBytes + 1).ToString(), args);
            Assert.DoesNotContain(args, arg => arg.Contains("/private/"));
        }
        foreach (var args in step.ReferenceCommands)
        {
            Assert.Contains("48000", args);
            Assert.Contains("pcm_f32le", args);
            Assert.Equal(RemoteQualityContract.ReferencePlaceholder, args[^1]);
            Assert.Contains("-n", args);
        }
    }

    [Fact]
    public void Image_step_compares_the_whole_original_and_names_the_real_encoder_control()
    {
        var contract = new RemotePerceptualSearchContract(Guid.NewGuid(), MediaKind.Image, "libwebp", PerceptualQualityPlan.Image("libwebp", 80),
            VerificationPolicy.Default with { ImagePerceptualGateEnabled = true, MinimumImagePerceptualScore = 85 },
            [], SourceImage: new(32, 32, true));
        var spec = new TranscodeSpec("/private/photo.png", "/private/photo.webp", null, null, null, false,
            Kind: MediaKind.Image, ImageEncoder: "libwebp", ImageQuality: 80);
        var step = RemotePerceptualSearchPlanner.Plan(contract, 60, 1, spec);
        var args = Assert.Single(step.SampleCommands);
        Assert.Empty(step.ReferenceCommands);
        Assert.Contains("libwebp", args);
        Assert.Contains("-quality", args);
        Assert.Contains("60", args);
        Assert.DoesNotContain("-ss", args);
        Assert.DoesNotContain("-lossless", args);
        Assert.Equal(WorkerProtocol.OutputPlaceholder + ".webp", args[^1]);
        Assert.Throws<ArgumentException>(() => RemotePerceptualSearchPlanner.Plan(contract, 60, 1, spec with { ImageLossless = true }));
        Assert.Throws<ArgumentException>(() => RemotePerceptualSearchPlanner.Plan(contract, 60, 1, spec with { ImageScaleFilter = "scale=16:16" }));
        Assert.Throws<ArgumentException>(() => RemotePerceptualSearchPlanner.Plan(contract, 60, 4, spec));
        Assert.Throws<ArgumentException>(() => RemotePerceptualSearchPlanner.Plan(contract, 101, 1, spec));
    }
}
