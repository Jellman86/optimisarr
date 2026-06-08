using Optimisarr.Core.Tools;

namespace Optimisarr.Tests;

public sealed class HardwareCapabilityParserTests
{
    [Fact]
    public void Parses_ffmpeg_hwaccels_output()
    {
        const string output = """
            Hardware acceleration methods:
            vdpau
            cuda
            vaapi
            qsv
            """;

        var accelerators = HardwareCapabilityParser.ParseHardwareAccelerators(output);

        Assert.Equal(["vdpau", "cuda", "vaapi", "qsv"], accelerators);
    }

    private const string EncoderListing = """
         V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10
         V....D libx265              libx265 H.265 / HEVC
         V....D h264_nvenc           NVIDIA NVENC H.264 encoder
         V....D hevc_nvenc           NVIDIA NVENC HEVC encoder
         V....D hevc_qsv             HEVC QSV encoder
        """;

    [Fact]
    public void Maps_known_encoder_availability_when_hardware_is_present()
    {
        var encoders = HardwareCapabilityParser.ParseEncoders(
            EncoderListing, nvidiaRuntimeAvailable: true, driDeviceAvailable: true);

        Assert.Contains(encoders, encoder => encoder is { Name: "libx264", Codec: "h264", Mode: "CPU", Available: true });
        Assert.Contains(encoders, encoder => encoder is { Name: "h264_nvenc", Codec: "h264", Mode: "NVIDIA NVENC", Available: true });
        Assert.Contains(encoders, encoder => encoder is { Name: "hevc_qsv", Codec: "hevc", Mode: "Intel QSV", Available: true });
        Assert.Contains(encoders, encoder => encoder is { Name: "av1_nvenc", Codec: "av1", Mode: "NVIDIA NVENC", Available: false });
    }

    [Fact]
    public void Listed_nvenc_encoder_is_unavailable_without_nvidia_runtime()
    {
        var encoders = HardwareCapabilityParser.ParseEncoders(
            EncoderListing, nvidiaRuntimeAvailable: false, driDeviceAvailable: true);

        Assert.Contains(encoders, encoder => encoder is { Name: "hevc_nvenc", Mode: "NVIDIA NVENC", Available: false });
        Assert.Contains(encoders, encoder => encoder is { Name: "h264_nvenc", Mode: "NVIDIA NVENC", Available: false });
    }

    [Fact]
    public void Listed_qsv_encoder_is_unavailable_without_dri_device()
    {
        var encoders = HardwareCapabilityParser.ParseEncoders(
            EncoderListing, nvidiaRuntimeAvailable: true, driDeviceAvailable: false);

        Assert.Contains(encoders, encoder => encoder is { Name: "hevc_qsv", Mode: "Intel QSV", Available: false });
    }

    [Fact]
    public void Cpu_encoders_stay_available_without_any_hardware()
    {
        var encoders = HardwareCapabilityParser.ParseEncoders(
            EncoderListing, nvidiaRuntimeAvailable: false, driDeviceAvailable: false);

        Assert.Contains(encoders, encoder => encoder is { Name: "libx264", Mode: "CPU", Available: true });
        Assert.Contains(encoders, encoder => encoder is { Name: "libx265", Mode: "CPU", Available: true });
    }
}
