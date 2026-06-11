using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class PacketTimestampParserTests
{
    [Fact]
    public void A_strictly_increasing_stream_has_no_regressions()
    {
        const string csv = """
        0.000000
        0.041708
        0.083417
        0.125125
        """;

        var integrity = PacketTimestampParser.Parse(csv);

        Assert.Equal(4, integrity.TimestampCount);
        Assert.Equal(0, integrity.NonMonotonicCount);
        Assert.Null(integrity.FirstRegressionDetail);
    }

    [Fact]
    public void Equal_consecutive_timestamps_are_not_a_regression()
    {
        // Decode timestamps may repeat (e.g. two packets sharing a DTS); only a
        // backward step is a real container fault.
        const string csv = """
        0.000000
        0.041708
        0.041708
        0.083417
        """;

        var integrity = PacketTimestampParser.Parse(csv);

        Assert.Equal(0, integrity.NonMonotonicCount);
    }

    [Fact]
    public void A_backward_step_is_counted_and_described()
    {
        const string csv = """
        0.000000
        0.083417
        0.041708
        0.125125
        """;

        var integrity = PacketTimestampParser.Parse(csv);

        Assert.Equal(1, integrity.NonMonotonicCount);
        Assert.Contains("0.041708", integrity.FirstRegressionDetail);
        Assert.Contains("0.083417", integrity.FirstRegressionDetail);
    }

    [Fact]
    public void Several_regressions_are_all_counted_but_only_the_first_is_described()
    {
        const string csv = """
        0.000000
        0.083417
        0.041708
        0.200000
        0.150000
        """;

        var integrity = PacketTimestampParser.Parse(csv);

        Assert.Equal(2, integrity.NonMonotonicCount);
        Assert.Contains("0.041708", integrity.FirstRegressionDetail);
    }

    [Fact]
    public void Missing_timestamps_are_skipped_without_breaking_monotonicity()
    {
        // "N/A" packets carry no DTS; they must not be read as a backward jump to zero.
        const string csv = """
        0.000000
        0.041708
        N/A
        0.083417
        """;

        var integrity = PacketTimestampParser.Parse(csv);

        Assert.Equal(3, integrity.TimestampCount);
        Assert.Equal(0, integrity.NonMonotonicCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_input_is_a_clean_zero_count(string? csv)
    {
        var integrity = PacketTimestampParser.Parse(csv);

        Assert.Equal(0, integrity.TimestampCount);
        Assert.Equal(0, integrity.NonMonotonicCount);
        Assert.Null(integrity.FirstRegressionDetail);
    }
}
