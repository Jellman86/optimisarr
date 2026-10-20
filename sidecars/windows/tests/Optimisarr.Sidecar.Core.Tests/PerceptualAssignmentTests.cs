using System.Text.Json;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;
using Optimisarr.Sidecar.Core.Session;
using WorkerProtocol = Optimisarr.Core.Workers.WorkerProtocol;

namespace Optimisarr.Sidecar.Core.Tests;

internal static class PerceptualJobFixture
{
    internal static Assignment Assignment(bool image = true)
    {
        var policy = VerificationPolicy.Default with { RequireSizeReduction = false, ImagePerceptualGateEnabled = true,
            MinimumImagePerceptualScore = 85, ImageQualityGateEnabled = false, AudioQualityGateEnabled = true, MaximumAudioQualityDistance = 0.02 };
        var kind = image ? MediaKind.Image : MediaKind.Audio;
        var encoder = image ? "libwebp" : "aac";
        var contract = new RemotePerceptualSearchContract(Guid.NewGuid(), kind, encoder,
            image ? PerceptualQualityPlan.Image(encoder, 80) : PerceptualQualityPlan.Audio(encoder, 128), policy,
            image ? [] : PerceptualSamplePlanner.AudioWindows(8), SourceAudio: image ? null : new(8, 2, 48000, "stereo"),
            SourceImage: image ? new(32, 32, false) : null);
        var spec = new TranscodeSpec(WorkerProtocol.InputPlaceholder, WorkerProtocol.OutputPlaceholder + (image ? ".webp" : ".m4a"),
            null, null, null, false, Kind: kind, AudioEncoder: image ? null : encoder, AudioBitrateKbps: image ? null : 128,
            ImageEncoder: image ? encoder : null, ImageQuality: image ? 80 : null);
        return new(Guid.NewGuid(), 77, "Native fixture", 12, null, "None", DateTimeOffset.UtcNow.AddMinutes(5), 10,
            FfmpegCommandBuilder.Build(spec, optimisedMarker: "worker-fixture"), image ? "webp" : "m4a",
            new(false, "", 1, false, 0, 0, []), FullVerification: new(image ? 4 : 2, Guid.NewGuid(), false,
                MeasureAudioQuality: !image, Image: image ? new(false, true, true) : null), Kind: kind, AudioEncoder: image ? null : encoder,
            PerceptualSearch: RemotePerceptualSearchPlanner.Plan(contract, contract.Plan.Baseline, 0, spec),
            ImageEncoder: image ? encoder : null, RequiredScratchBytes: WorkerScratchBudget.RequiredBytes(12, null, contract));
    }
}

public sealed class PerceptualAssignmentTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Native_quality_first_claims_round_trip_with_the_search_encoder_and_workspace_contract(bool image)
    {
        var assignment = PerceptualJobFixture.Assignment(image);
        var restored = JsonSerializer.Deserialize<Assignment>(JsonSerializer.Serialize(assignment, SidecarClient.Json), SidecarClient.Json)!;
        Assert.Null(restored.RefuseMediaContract()); Assert.NotNull(restored.PerceptualSearch);
        Assert.Equal(image ? "libwebp" : "aac", restored.Encoder);
        Assert.Equal(assignment.RequiredScratchBytes, restored.RequiredScratchBytes);
    }

    [Theory]
    [InlineData("no-search")]
    [InlineData("no-verification")]
    [InlineData("no-metric-gate")]
    [InlineData("small-workspace")]
    [InlineData("another-encoder")]
    [InlineData("another-output")]
    [InlineData("another-source")]
    [InlineData("another-kind")]
    [InlineData("video-quality")]
    [InlineData("old-image-verification")]
    public void Incomplete_or_conflicting_native_claims_are_refused_instead_of_running_the_saved_baseline(string invalid)
    {
        var a = PerceptualJobFixture.Assignment();
        a = invalid switch
        {
            "no-search" => a with { PerceptualSearch = null },
            "no-verification" => a with { FullVerification = null },
            "no-metric-gate" => a with { FullVerification = a.FullVerification! with { Image = new(false, true, false) } },
            "small-workspace" => a with { RequiredScratchBytes = 100 },
            "another-encoder" => a with { ImageEncoder = "mjpeg" },
            "another-output" => a with { OutputExtension = "jpg" },
            "another-source" => a with { Arguments = a.Arguments.Select(arg => arg == "{{input}}" ? "/another/source" : arg).ToArray() },
            "another-kind" => a with { Kind = MediaKind.Video, VideoEncoder = "hevc_nvenc" },
            "video-quality" => a with { Quality = a.Quality with { Measure = true } },
            "old-image-verification" => a with { FullVerification = a.FullVerification! with { Version = 2 } },
            _ => throw new InvalidOperationException()
        };
        Assert.NotNull(a.RefuseMediaContract());
    }
}
