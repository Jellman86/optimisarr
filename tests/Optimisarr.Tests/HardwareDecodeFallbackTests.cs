using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class HardwareDecodeFallbackTests
{
    [Theory]
    [InlineData("[hevc_qsv @ 0x..] Failed setup for format qsv: hwaccel initialisation returned error")]
    [InlineData("Error while opening decoder for input stream")]
    [InlineData("Impossible to convert between the formats supported by the filter")]
    [InlineData("[AVHWDeviceContext] Device creation failed: -22")]
    public void Retries_in_software_for_a_hardware_decode_failure(string stderr)
    {
        Assert.True(HardwareDecodeFallback.ShouldRetryInSoftware(stderr));
    }

    [Theory]
    [InlineData("av_interleaved_write_frame(): No space left on device")]
    [InlineData("Conversion failed!")]
    [InlineData("Output file is empty, nothing was encoded")]
    public void Does_not_retry_for_an_unrelated_failure(string stderr)
    {
        Assert.False(HardwareDecodeFallback.ShouldRetryInSoftware(stderr));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Does_not_retry_when_there_is_no_error_text(string? stderr)
    {
        Assert.False(HardwareDecodeFallback.ShouldRetryInSoftware(stderr));
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        Assert.True(HardwareDecodeFallback.ShouldRetryInSoftware("FAILED SETUP FOR FORMAT QSV"));
    }
}
