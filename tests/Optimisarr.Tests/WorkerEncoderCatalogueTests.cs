using Optimisarr.Core.Queue;
using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

/// <summary>
/// A sidecar advertises encoder names; the selector wants codec and family. The catalogue is the
/// bridge, and it fails closed: a name it has not been taught is not offered, because an encoder
/// nothing here can build arguments for is one no assignment could execute.
/// </summary>
public sealed class WorkerEncoderCatalogueTests
{
    [Theory]
    [InlineData("libx265", "hevc", "CPU")]
    [InlineData("libx264", "h264", "CPU")]
    [InlineData("libsvtav1", "av1", "CPU")]
    [InlineData("hevc_nvenc", "hevc", "NVIDIA NVENC")]
    [InlineData("h264_qsv", "h264", "Intel QSV")]
    [InlineData("av1_vaapi", "av1", "VAAPI")]
    [InlineData("hevc_videotoolbox", "hevc", "VideoToolbox")]
    [InlineData("h264_videotoolbox", "h264", "VideoToolbox")]
    public void A_known_encoder_name_resolves_to_its_codec_and_family(string name, string codec, string mode)
    {
        var capability = Assert.Single(WorkerEncoderCatalogue.Describe([name]));

        Assert.Equal(name, capability.Name);
        Assert.Equal(codec, capability.Codec);
        Assert.Equal(mode, capability.Mode);
        Assert.True(capability.Available);
    }

    [Fact]
    public void An_unknown_name_is_dropped_rather_than_guessed()
    {
        var described = WorkerEncoderCatalogue.Describe(["libx265", "mystery_encoder", "prores_ks"]);

        var only = Assert.Single(described);
        Assert.Equal("libx265", only.Name);
    }

    [Fact]
    public void Names_are_matched_case_insensitively_and_trimmed()
    {
        var capability = Assert.Single(WorkerEncoderCatalogue.Describe([" HEVC_VideoToolbox "]));

        Assert.Equal("hevc_videotoolbox", capability.Name);
    }

    [Fact]
    public void A_mac_that_proves_videotoolbox_is_offered_it_ahead_of_its_cpu_encoder()
    {
        // The whole point of a sidecar is idle hardware; a proved hardware encoder wins.
        var capabilities = WorkerEncoderCatalogue.Describe(["libx265", "hevc_videotoolbox"]);

        var selection = EncoderSelector.Select("hevc", EncoderMode.Auto, capabilities);

        Assert.True(selection.Succeeded);
        Assert.Equal("hevc_videotoolbox", selection.EncoderName);
    }

    [Fact]
    public void Cpu_mode_chooses_the_workers_cpu_encoder_even_when_it_proved_nvenc()
    {
        // #425: the queue's CPU mode is a software-encoding policy, not a description of the
        // server's GPU. A worker that proved both families must be given its CPU encoder.
        var selection = WorkerEncoderCatalogue.Select(
            "hevc", EncoderMode.Cpu, ["libx265", "hevc_nvenc"]);

        Assert.True(selection.Succeeded);
        Assert.Equal("libx265", selection.EncoderName);
    }

    [Fact]
    public void Cpu_mode_refuses_a_worker_that_proved_no_cpu_encoder()
    {
        var selection = WorkerEncoderCatalogue.Select(
            "hevc", EncoderMode.Cpu, ["hevc_nvenc"]);

        Assert.False(selection.Succeeded);
    }

    [Fact]
    public void A_vendor_mode_names_the_servers_gpu_so_the_worker_keeps_its_own_best_hardware()
    {
        // The server's NVENC choice must not strand an Intel worker: it uses the best hardware
        // it proved, exactly as Auto would choose for it.
        var selection = WorkerEncoderCatalogue.Select(
            "hevc", EncoderMode.NvidiaNvenc, ["libx265", "hevc_qsv"]);

        Assert.True(selection.Succeeded);
        Assert.Equal("hevc_qsv", selection.EncoderName);
    }

    [Theory]
    [InlineData(EncoderMode.Cpu)]
    [InlineData(EncoderMode.Auto)]
    public void A_ten_bit_h264_source_keeps_the_worker_on_its_cpu_encoder(EncoderMode mode)
    {
        var selection = WorkerEncoderCatalogue.Select(
            "h264", mode, ["libx264", "h264_nvenc"], sourceBitDepth: 10);

        Assert.True(selection.Succeeded);
        Assert.Equal("libx264", selection.EncoderName);
    }

    [Fact]
    public void Auto_mode_still_prefers_the_workers_proved_hardware_encoder()
    {
        var selection = WorkerEncoderCatalogue.Select(
            "hevc", EncoderMode.Auto, ["libx265", "hevc_nvenc"]);

        Assert.True(selection.Succeeded);
        Assert.Equal("hevc_nvenc", selection.EncoderName);
    }
}
