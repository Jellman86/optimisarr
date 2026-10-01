using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class VmafProductionPolicyTests
{
    [Theory]
    [InlineData(1920, 1080, "vmaf_v1.0.16_3d0h")]
    [InlineData(3840, 1600, "vmaf_v1.0.16_1d5h_2160")]
    [InlineData(3840, 2160, "vmaf_v1.0.16_1d5h_2160")]
    public void Ordinary_SDR_defaults_to_v1_using_the_actual_candidate_format(int width, int height, string model)
    {
        var context = new QualityMeasurementContext(width, height, false, false,
            ReferenceFrameRate: 24, EncodedVideo: new(1280, 720, 8), Acceleration: VmafAcceleration.Cuda);
        var command = QualityScoreCommandBuilder.Build("candidate", "source", "log", context, 2);
        Assert.Equal(model, command.ModelVersion);
        Assert.Contains("cambi.enc_width=1280", command.FilterGraph);
        Assert.Contains("format=yuv420p10le", command.FilterGraph);
        Assert.DoesNotContain("-hwaccel", command.Arguments);
        Assert.Equal(VmafAcceleration.None, QualityScoreCommandBuilder.EffectiveAcceleration(context));
    }

    [Theory]
    [InlineData(true, 24)]
    [InlineData(false, 60)]
    public void HDR_and_high_frame_rate_material_retain_the_supported_legacy_policy(bool hdr, double fps)
    {
        var context = new QualityMeasurementContext(1920, 1080, hdr, false,
            ReferenceFrameRate: fps, EncodedVideo: new(1920, 1080, 10));
        Assert.Equal("vmaf_v0.6.1", QualityScoreCommandBuilder.Build("d", "r", "l", context, 2).ModelVersion);
    }

    [Fact]
    public void SDR_without_actual_candidate_metadata_fails_instead_of_silently_using_legacy()
    {
        Assert.Throws<ArgumentException>(() => QualityScoreCommandBuilder.Build("d", "r", "l",
            new(1920, 1080, false, false, ReferenceFrameRate: 24), 2));
    }
}
