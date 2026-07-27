using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class HardwareToneMapPolicyTests
{
    [Theory]
    [InlineData("hevc_qsv", "smpte2084")]
    [InlineData("hevc_vaapi", "SMPTE2084")]
    public void Hardware_tone_mapping_is_used_for_a_confirmed_hdr10_job(
        string encoder,
        string colorTransfer)
    {
        Assert.True(HardwareToneMapPolicy.ShouldUse(
            HdrToneMapMode.Hardware,
            hardwareDecode: true,
            toneMapToSdr: true,
            vmafQualityGateEnabled: false,
            sourceIsDolbyVision: false,
            sourceColorTransfer: colorTransfer,
            videoEncoder: encoder));
    }

    [Theory]
    [InlineData(HdrToneMapMode.Software, true, true, false, false, "smpte2084", "hevc_qsv")]
    [InlineData(HdrToneMapMode.Hardware, false, true, false, false, "smpte2084", "hevc_qsv")]
    [InlineData(HdrToneMapMode.Hardware, true, false, false, false, "smpte2084", "hevc_qsv")]
    [InlineData(HdrToneMapMode.Hardware, true, true, true, false, "smpte2084", "hevc_qsv")]
    [InlineData(HdrToneMapMode.Hardware, true, true, false, true, "smpte2084", "hevc_qsv")]
    [InlineData(HdrToneMapMode.Hardware, true, true, false, false, "arib-std-b67", "hevc_qsv")]
    [InlineData(HdrToneMapMode.Hardware, true, true, false, false, null, "hevc_qsv")]
    [InlineData(HdrToneMapMode.Hardware, true, true, false, false, "smpte2084", "hevc_nvenc")]
    [InlineData(HdrToneMapMode.Hardware, true, true, false, false, "smpte2084", "libx265")]
    public void Incompatible_jobs_keep_the_software_tone_map(
        HdrToneMapMode mode,
        bool hardwareDecode,
        bool toneMapToSdr,
        bool vmafQualityGateEnabled,
        bool sourceIsDolbyVision,
        string? sourceColorTransfer,
        string encoder)
    {
        Assert.False(HardwareToneMapPolicy.ShouldUse(
            mode,
            hardwareDecode,
            toneMapToSdr,
            vmafQualityGateEnabled,
            sourceIsDolbyVision,
            sourceColorTransfer,
            encoder));
    }
}
