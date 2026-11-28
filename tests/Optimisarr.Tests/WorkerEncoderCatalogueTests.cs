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
    [InlineData("hevc_amf", "hevc", "AMD AMF")]
    [InlineData("h264_amf", "h264", "AMD AMF")]
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
            "hevc", EncoderMode.Cpu, ["libx265", "hevc_nvenc"], WorkerProtocol.Current);

        Assert.True(selection.Succeeded);
        Assert.Equal("libx265", selection.EncoderName);
    }

    [Fact]
    public void Cpu_mode_refuses_a_worker_that_proved_no_cpu_encoder()
    {
        var selection = WorkerEncoderCatalogue.Select(
            "hevc", EncoderMode.Cpu, ["hevc_nvenc"], WorkerProtocol.Current);

        Assert.False(selection.Succeeded);
    }

    [Fact]
    public void A_vendor_mode_names_the_servers_gpu_so_the_worker_keeps_its_own_best_hardware()
    {
        // The server's NVENC choice must not strand an Intel worker: it uses the best hardware
        // it proved, exactly as Auto would choose for it.
        var selection = WorkerEncoderCatalogue.Select(
            "hevc", EncoderMode.NvidiaNvenc, ["libx265", "hevc_qsv"], WorkerProtocol.Current);

        Assert.True(selection.Succeeded);
        Assert.Equal("hevc_qsv", selection.EncoderName);
    }

    [Theory]
    [InlineData(EncoderMode.Cpu)]
    [InlineData(EncoderMode.Auto)]
    public void A_ten_bit_h264_source_keeps_the_worker_on_its_cpu_encoder(EncoderMode mode)
    {
        var selection = WorkerEncoderCatalogue.Select(
            "h264", mode, ["libx264", "h264_nvenc"], WorkerProtocol.Current, sourceBitDepth: 10);

        Assert.True(selection.Succeeded);
        Assert.Equal("libx264", selection.EncoderName);
    }

    [Fact]
    public void Auto_mode_still_prefers_the_workers_proved_hardware_encoder()
    {
        var selection = WorkerEncoderCatalogue.Select(
            "hevc", EncoderMode.Auto, ["libx265", "hevc_nvenc"], WorkerProtocol.Current);

        Assert.True(selection.Succeeded);
        Assert.Equal("hevc_nvenc", selection.EncoderName);
    }

    [Fact]
    public void A_worker_is_given_another_encoder_for_a_job_intel_qsv_failed()
    {
        var selection = WorkerEncoderCatalogue.Select(
            "hevc", EncoderMode.Auto, ["libx265", "hevc_qsv", "hevc_vaapi"], WorkerProtocol.Current, avoidIntelQsv: true);

        Assert.Equal("hevc_vaapi", selection.EncoderName);
    }

    [Fact]
    public void An_amd_worker_is_offered_its_proved_amf_encoder_under_auto_and_vendor_amd()
    {
        Assert.Equal("hevc_amf", WorkerEncoderCatalogue.Select(
            "hevc", EncoderMode.Auto, ["libx265", "hevc_amf"], WorkerProtocol.Current).EncoderName);

        Assert.Equal("hevc_amf", WorkerEncoderCatalogue.Select(
            "hevc", EncoderMode.AmdAmf, ["libx265", "hevc_amf"], WorkerProtocol.Current).EncoderName);

        // The server naming AMD must not strand a worker that only proved Intel (as with NVENC).
        Assert.Equal("hevc_qsv", WorkerEncoderCatalogue.Select(
            "hevc", EncoderMode.AmdAmf, ["libx265", "hevc_qsv"], WorkerProtocol.Current).EncoderName);
    }

    [Fact]
    public void An_amd_worker_on_an_older_protocol_keeps_encoding_with_its_cpu_encoder()
    {
        // A sidecar before protocol 16 advertises its proved AMF encoders but refuses AMF's -qp_i and
        // -qp_p, so it keeps the work it already did rather than being handed jobs it would refuse.
        var selection = WorkerEncoderCatalogue.Select(
            "hevc", EncoderMode.Auto, ["libx265", "hevc_amf"], WorkerProtocol.AmfConstantQualityVersion - 1);

        Assert.Equal("libx265", selection.EncoderName);
    }

    [Fact]
    public void An_amd_only_worker_on_an_older_protocol_is_not_offered_the_job()
    {
        var selection = WorkerEncoderCatalogue.Select(
            "hevc", EncoderMode.Auto, ["hevc_amf"], WorkerProtocol.AmfConstantQualityVersion - 1);

        Assert.False(selection.Succeeded);
    }

    [Fact]
    public void A_mac_worker_before_the_keyframe_interval_protocol_keeps_encoding_with_its_cpu_encoder()
    {
        // Every VideoToolbox encode now carries a GOP length (#612), which a sidecar before
        // protocol 15 refuses as an unknown option.
        var selection = WorkerEncoderCatalogue.Select(
            "hevc", EncoderMode.Auto, ["libx265", "hevc_videotoolbox"], WorkerProtocol.KeyframeIntervalVersion - 1);

        Assert.Equal("libx265", selection.EncoderName);
    }

    [Fact]
    public void A_videotoolbox_only_worker_before_the_keyframe_interval_protocol_is_not_offered_the_job() =>
        Assert.False(WorkerEncoderCatalogue.Select(
            "hevc", EncoderMode.Auto, ["hevc_videotoolbox"], WorkerProtocol.KeyframeIntervalVersion - 1).Succeeded);

    [Fact]
    public void A_mac_worker_on_the_keyframe_interval_protocol_is_given_videotoolbox() =>
        Assert.Equal("hevc_videotoolbox", WorkerEncoderCatalogue.Select(
            "hevc", EncoderMode.Auto, ["libx265", "hevc_videotoolbox"], WorkerProtocol.KeyframeIntervalVersion).EncoderName);

    [Fact]
    public void An_amd_av1_encoder_is_not_offered_because_its_quantiser_scale_is_not_the_quality_scale()
    {
        // av1_amf reads -qp_i/-qp_p as a 0-255 quantiser index, not the 0-51 scale Optimisarr's
        // quality number is on, so the same number would mean a different, untested quality.
        Assert.Empty(WorkerEncoderCatalogue.Describe(["av1_amf"]));
        Assert.Equal("libsvtav1", WorkerEncoderCatalogue.Select(
            "av1", EncoderMode.Auto, ["libsvtav1", "av1_amf"], WorkerProtocol.Current).EncoderName);
    }
}
