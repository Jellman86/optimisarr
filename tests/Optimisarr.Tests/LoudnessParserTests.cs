using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class LoudnessParserTests
{
    [Fact]
    public void Reads_integrated_loudness_from_an_ebur128_summary()
    {
        const string stderr = """
        [Parsed_ebur128_0 @ 0x55] Summary:

          Integrated loudness:
            I:         -23.4 LUFS
            Threshold: -33.6 LUFS

          Loudness range:
            LRA:         7.2 LU
        """;

        Assert.Equal(-23.4, LoudnessParser.ParseIntegratedLufs(stderr));
    }

    [Fact]
    public void Reads_a_positive_or_zero_value()
    {
        Assert.Equal(-5.0, LoudnessParser.ParseIntegratedLufs("    I:  -5.0 LUFS"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no loudness here")]
    [InlineData("LRA: 7.2 LU")]
    public void Returns_null_without_an_integrated_line(string stderr)
    {
        Assert.Null(LoudnessParser.ParseIntegratedLufs(stderr));
    }

    [Fact]
    public void Reads_true_peak_from_an_ebur128_peak_summary()
    {
        const string stderr = """
        [Parsed_ebur128_0 @ 0x55] Summary:

          Integrated loudness:
            I:         -23.4 LUFS

          True peak:
            Peak:       -1.5 dBFS
        """;

        Assert.Equal(-1.5, LoudnessParser.ParseTruePeakDbtp(stderr));
    }

    [Fact]
    public void Reads_a_clipping_true_peak_above_zero()
    {
        Assert.Equal(0.8, LoudnessParser.ParseTruePeakDbtp("    Peak:  0.8 dBFS"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("I: -23.4 LUFS")]
    [InlineData("no peak here")]
    public void Returns_null_without_a_true_peak_line(string stderr)
    {
        Assert.Null(LoudnessParser.ParseTruePeakDbtp(stderr));
    }
}
