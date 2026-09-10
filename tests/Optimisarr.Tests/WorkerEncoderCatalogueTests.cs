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
}
