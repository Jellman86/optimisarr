using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class EncoderQualityPolicyTests
{
    [Theory]
    [InlineData("libx265", 24, 24, "CRF")]
    [InlineData("hevc_qsv", 24, 20, "ICQ")]
    [InlineData("hevc_nvenc", 24, 20, "CQ")]
    [InlineData("hevc_vaapi", 24, 20, "QP")]
    public void Hardware_encoders_receive_conservative_quality_headroom(
        string encoder, int requested, int expected, string mode)
    {
        var quality = EncoderQualityPolicy.Resolve(encoder, requested, retryCount: 0);

        Assert.Equal(requested, quality.Requested);
        Assert.Equal(expected, quality.Effective);
        Assert.Equal(mode, quality.Mode);
    }

    [Fact]
    public void A_quality_retry_raises_quality_without_leaving_the_valid_range()
    {
        Assert.Equal(17, EncoderQualityPolicy.Resolve("hevc_qsv", 24, retryCount: 1).Effective);
        Assert.Equal(0, EncoderQualityPolicy.Resolve("hevc_qsv", 2, retryCount: 4).Effective);
    }
}
