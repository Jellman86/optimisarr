using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class HardwareToneMapFallbackTests
{
    [Theory]
    [InlineData("Error initializing a MFX session: MFX_ERR_UNSUPPORTED")]
    [InlineData("Error sending frames to consumers: Function not implemented")]
    [InlineData("No such filter: 'tonemap_vaapi'")]
    [InlineData("Failed to configure output pad on Parsed_vpp_qsv_0")]
    public void Hardware_filter_failures_retry_with_software_tone_mapping(string stderr)
    {
        Assert.True(HardwareToneMapFallback.ShouldRetryInSoftware(stderr));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("No space left on device")]
    [InlineData("Permission denied")]
    public void Unrelated_failures_do_not_repeat_an_expensive_encode(string? stderr)
    {
        Assert.False(HardwareToneMapFallback.ShouldRetryInSoftware(stderr));
    }
}
