using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class LoudnessParserTests
{
    [Fact]
    public void Integrated_loudness_uses_the_final_summary_not_the_first_progress_value()
    {
        const string log = """
            [Parsed_ebur128_0] t: 0.1 TARGET:-23 LUFS M:-120.7 S:-120.7 I: -70.0 LUFS LRA:0.0 LU
            [Parsed_ebur128_0] Summary:
              Integrated loudness:
                I: -34.2 LUFS
              True peak:
                Peak: -23.9 dBFS
            """;
        Assert.Equal(-34.2, LoudnessParser.ParseIntegratedLufs(log));
        Assert.Equal(-23.9, LoudnessParser.ParseTruePeakDbtp(log));
    }

    [Fact]
    public void A_later_incomplete_summary_cannot_reuse_earlier_measurements()
    {
        const string log = "Summary:\n I: -23.0 LUFS\n True peak:\n Peak: -1.0 dBFS\nSummary:\n I: -inf LUFS\n True peak:\n Peak: -inf dBFS";
        Assert.Null(LoudnessParser.ParseIntegratedLufs(log));
        Assert.Null(LoudnessParser.ParseTruePeakDbtp(log));
    }

    [Fact]
    public void True_peak_is_not_confused_with_a_sample_peak_in_the_same_summary()
    {
        const string log = "Summary:\n Sample peak:\n Peak: -3.0 dBFS\n True peak:\n Peak: 0.2 dBFS";
        Assert.Equal(0.2, LoudnessParser.ParseTruePeakDbtp(log));
    }

    [Fact]
    public void A_final_negative_infinite_true_peak_explicitly_records_silence_without_inventing_a_number()
    {
        const string log = "Summary:\n I: -70.0 LUFS\n True peak:\n Peak: -inf dBFS";
        Assert.Null(LoudnessParser.ParseTruePeakDbtp(log));
        Assert.True(LoudnessParser.IsSilentTruePeak(log));
        Assert.False(LoudnessParser.IsSilentTruePeak("Summary:\n Sample peak:\n Peak: -inf dBFS"));
        Assert.False(LoudnessParser.IsSilentTruePeak(log + "\nSummary:\n True peak:\n Peak: invalid dBFS"));
    }
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
