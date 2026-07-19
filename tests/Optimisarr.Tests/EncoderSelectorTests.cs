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
    public void Auto_uses_cpu_h264_when_hardware_cannot_preserve_source_bit_depth()
    {
        var selection = EncoderSelector.Select(
            "h264", EncoderMode.Auto, Capabilities, sourceBitDepth: 10);

        Assert.True(selection.Succeeded);
        Assert.Equal("libx264", selection.EncoderName);
    }

    [Fact]
    public void Auto_keeps_hardware_h264_for_an_eight_bit_source()
    {
        var selection = EncoderSelector.Select(
            "h264", EncoderMode.Auto, Capabilities, sourceBitDepth: 8);

        Assert.True(selection.Succeeded);
        Assert.Equal("h264_qsv", selection.EncoderName);
    }

    [Fact]
    public void Forced_hardware_h264_rejects_a_source_whose_depth_it_cannot_preserve()
    {
        var selection = EncoderSelector.Select(
            "h264", EncoderMode.IntelQsv, Capabilities, sourceBitDepth: 10);

        Assert.False(selection.Succeeded);
        Assert.Contains("cannot preserve", selection.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("10-bit", selection.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void H264_fails_closed_when_no_supported_encoder_can_preserve_the_source_depth()
    {
        var selection = EncoderSelector.Select(
            "h264", EncoderMode.Auto, Capabilities, sourceBitDepth: 12);

        Assert.False(selection.Succeeded);
        Assert.Contains("No supported H.264 encoder", selection.Error, StringComparison.Ordinal);
        Assert.Contains("12-bit", selection.Error, StringComparison.OrdinalIgnoreCase);
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
