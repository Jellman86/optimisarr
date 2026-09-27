using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class VmafV1PreparationTests
{
    private static QualityMeasurementContext Context => new(1920, 1080, false, false,
        ModelVersion: "vmaf_v1.0.16_3d0h", EncodedVideo: new(1280, 720, 8));

    [Fact]
    public void V1_measures_ten_bit_SDR_and_tells_CAMBI_the_actual_encode_format()
    {
        var command = QualityScoreCommandBuilder.Build("d", "r", "l", Context, 4);
        Assert.Equal(2, command.FilterGraph.Split("format=yuv420p10le").Length - 1);
        Assert.Contains("cambi.enc_width=1280", command.FilterGraph);
        Assert.Contains("cambi.enc_height=720", command.FilterGraph);
        Assert.Contains("cambi.enc_bitdepth=8", command.FilterGraph);
        Assert.Contains("model='version=vmaf_v1.0.16_3d0h\\:cambi.enc_width=1280\\:cambi.enc_height=720\\:cambi.enc_bitdepth=8':", command.FilterGraph);
        Assert.Contains("10-bit", command.Preprocessing);
    }

    [Theory]
    [InlineData(VmafAcceleration.Cuda)]
    [InlineData(VmafAcceleration.Qsv)]
    [InlineData(VmafAcceleration.Vaapi)]
    public void V1_uses_the_validated_CPU_path_until_accelerated_ten_bit_parity_is_established(VmafAcceleration requested)
    {
        var context = Context with { Acceleration = requested };
        var command = QualityScoreCommandBuilder.Build("d", "r", "l", context, 4);
        Assert.Equal(VmafAcceleration.None, QualityScoreCommandBuilder.EffectiveAcceleration(context));
        Assert.DoesNotContain("-hwaccel", command.Arguments);
        Assert.DoesNotContain("libvmaf_cuda", command.FilterGraph);
    }

    [Fact]
    public void V1_rejects_unknown_encode_parameters_instead_of_assuming_ten_bit()
    {
        Assert.Throws<ArgumentException>(() => QualityScoreCommandBuilder.Build("d", "r", "l", Context with { EncodedVideo = null }, 4));
    }

    [Theory]
    [InlineData(0, 720, 8)]
    [InlineData(1280, -1, 8)]
    [InlineData(1280, 720, 7)]
    [InlineData(1280, 720, 12)]
    public void V1_rejects_unsupported_encode_parameters(int width, int height, int depth)
    {
        Assert.Throws<ArgumentException>(() => QualityScoreCommandBuilder.Build("d", "r", "l",
            Context with { EncodedVideo = new(width, height, depth) }, 4));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V1_does_not_silently_claim_HDR_calibration(bool tonemapped)
    {
        Assert.Throws<ArgumentException>(() => QualityScoreCommandBuilder.Build("d", "r", "l",
            Context with { ReferenceIsHdr = true, HdrConvertedToSdr = tonemapped }, 4));
    }

    [Fact]
    public void Existing_production_measurements_keep_their_calibrated_model_and_precision()
    {
        var command = QualityScoreCommandBuilder.Build("d", "r", "l", Context with { ModelVersion = null }, 4);
        Assert.Equal("vmaf_v0.6.1", command.ModelVersion);
        Assert.DoesNotContain("10le", command.FilterGraph);
        Assert.DoesNotContain("cambi", command.FilterGraph);
    }
}
