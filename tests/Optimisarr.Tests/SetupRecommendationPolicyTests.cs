using Optimisarr.Core.Queue;
using Optimisarr.Core.Settings;
using Optimisarr.Core.Tools;

namespace Optimisarr.Tests;

public sealed class SetupRecommendationPolicyTests
{
    [Fact]
    public void Nvidia_with_cuda_vmaf_recommends_hardware_decode_and_balanced_quality()
    {
        var result = SetupRecommendationPolicy.Recommend(
            [new EncoderCapability("hevc_nvenc", "hevc", "NVIDIA NVENC", Available: true)],
            vmafAvailable: true,
            cudaVmafAvailable: true);

        Assert.Equal(EncoderMode.NvidiaNvenc, result.EncoderMode);
        Assert.True(result.HardwareDecode);
        Assert.Equal(SetupVmafTier.Balanced, result.VmafTier);
        Assert.Equal(new TimeOnly(1, 0), result.ScheduleStart);
        Assert.Equal(new TimeOnly(6, 0), result.ScheduleEnd);
    }

    [Fact]
    public void Intel_is_preferred_to_vaapi_and_keeps_cpu_vmaf_off_by_default()
    {
        var result = SetupRecommendationPolicy.Recommend(
            [
                new EncoderCapability("hevc_vaapi", "hevc", "VAAPI", Available: true),
                new EncoderCapability("hevc_qsv", "hevc", "Intel QSV", Available: true)
            ],
            vmafAvailable: true,
            cudaVmafAvailable: false);

        Assert.Equal(EncoderMode.IntelQsv, result.EncoderMode);
        Assert.True(result.HardwareDecode);
        Assert.Equal(SetupVmafTier.Off, result.VmafTier);
        Assert.Equal("cpu-cost", result.VmafReason);
    }

    [Fact]
    public void Unproved_hardware_falls_back_to_cpu_and_never_enables_hardware_decode()
    {
        var result = SetupRecommendationPolicy.Recommend(
            [new EncoderCapability("hevc_nvenc", "hevc", "NVIDIA NVENC", Available: false)],
            vmafAvailable: false,
            cudaVmafAvailable: false);

        Assert.Equal(EncoderMode.Cpu, result.EncoderMode);
        Assert.False(result.HardwareDecode);
        Assert.Equal(SetupVmafTier.Off, result.VmafTier);
        Assert.Equal("unavailable", result.VmafReason);
    }

    [Fact]
    public void Hardware_without_the_default_hevc_codec_is_not_recommended()
    {
        var result = SetupRecommendationPolicy.Recommend(
            [new EncoderCapability("h264_nvenc", "h264", "NVIDIA NVENC", Available: true)],
            vmafAvailable: true,
            cudaVmafAvailable: true);

        Assert.Equal(EncoderMode.Cpu, result.EncoderMode);
        Assert.False(result.HardwareDecode);
        Assert.Equal(SetupVmafTier.Off, result.VmafTier);
    }
}
