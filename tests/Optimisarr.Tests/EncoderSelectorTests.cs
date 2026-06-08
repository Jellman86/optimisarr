using Optimisarr.Core.Queue;
using Optimisarr.Core.Tools;

namespace Optimisarr.Tests;

public sealed class EncoderSelectorTests
{
    private static readonly EncoderCapability[] Capabilities =
    [
        new("libx264", "h264", "CPU", true),
        new("libx265", "hevc", "CPU", true),
        new("libsvtav1", "av1", "CPU", false),
        new("hevc_nvenc", "hevc", "NVIDIA NVENC", true),
        new("h264_qsv", "h264", "Intel QSV", true),
        new("hevc_vaapi", "hevc", "VAAPI", false)
    ];

    [Fact]
    public void Auto_prefers_available_hardware_before_cpu()
    {
        var selection = EncoderSelector.Select("hevc", EncoderMode.Auto, Capabilities);

        Assert.True(selection.Succeeded);
        Assert.Equal("hevc_nvenc", selection.EncoderName);
    }

    [Fact]
    public void Cpu_mode_selects_cpu_encoder()
    {
        var selection = EncoderSelector.Select("hevc", EncoderMode.Cpu, Capabilities);

        Assert.True(selection.Succeeded);
        Assert.Equal("libx265", selection.EncoderName);
    }

    [Fact]
    public void Requested_hardware_mode_must_support_the_target_codec()
    {
        var selection = EncoderSelector.Select("av1", EncoderMode.NvidiaNvenc, Capabilities);

        Assert.False(selection.Succeeded);
        Assert.Contains("No available", selection.Error);
    }

    [Fact]
    public void Unknown_codec_fails_before_building_ffmpeg_args()
    {
        var selection = EncoderSelector.Select("vp9", EncoderMode.Auto, Capabilities);

        Assert.False(selection.Succeeded);
        Assert.Contains("No known encoder", selection.Error);
    }
}
