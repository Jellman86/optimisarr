using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class PixelFormatInfoTests
{
    [Theory]
    [InlineData("yuv420p", null, 8, 1)]
    [InlineData("yuv422p10le", null, 10, 2)]
    [InlineData("yuv444p12le", null, 12, 3)]
    [InlineData("p010le", null, 10, 1)]
    [InlineData("gbrp16le", null, 16, 3)]
    [InlineData("rgb48be", null, 16, 3)]
    [InlineData("nv12", 10, 10, 1)]
    public void Parses_common_ffmpeg_pixel_formats(
        string format, int? rawBits, int expectedDepth, int expectedChromaRank)
    {
        var result = PixelFormatInfo.Parse(format, rawBits);

        Assert.NotNull(result);
        Assert.Equal(expectedDepth, result.Value.BitDepth);
        Assert.Equal(expectedChromaRank, result.Value.ChromaRank);
    }
}
