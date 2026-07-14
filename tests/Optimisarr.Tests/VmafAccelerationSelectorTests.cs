using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class VmafAccelerationSelectorTests
{
    [Theory]
    [InlineData("hevc_nvenc", VmafAcceleration.Cuda)]
    [InlineData("h264_qsv", VmafAcceleration.Qsv)]
    [InlineData("hevc_vaapi", VmafAcceleration.Vaapi)]
    [InlineData("libx265", VmafAcceleration.None)]
    [InlineData(null, VmafAcceleration.None)]
    public void Hardware_encoder_selects_the_matching_vmaf_acceleration(
        string? encoder,
        VmafAcceleration expected)
    {
        Assert.Equal(expected, VmafAccelerationSelector.Select(encoder, hardwareDecodeEnabled: true));
    }

    [Fact]
    public void Disabled_hardware_decode_keeps_vmaf_on_the_software_path()
    {
        Assert.Equal(
            VmafAcceleration.None,
            VmafAccelerationSelector.Select("hevc_nvenc", hardwareDecodeEnabled: false));
    }
}
