using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class PacketTimestampParserTests
{
    // ffprobe emits "pts_time,dts_time,duration_time" per packet at -of csv=p=0.
    [Fact]
    public void A_strictly_increasing_stream_has_no_regressions()
    {
        const string csv = """
        0.000000,0.000000
        0.041708,0.041708
        0.083417,0.083417
        0.125125,0.125125
        """;

        var integrity = PacketTimestampParser.Parse(csv);

        Assert.Equal(4, integrity.TimestampCount);
        Assert.Equal(0, integrity.NonMonotonicCount);
        Assert.Null(integrity.FirstRegressionDetail);
        Assert.Equal(0.125125, integrity.LastPresentationSeconds);
    }

    [Fact]
    public void Equal_consecutive_decode_timestamps_are_not_a_regression()
    {
        // Decode timestamps may repeat; only a backward step is a real container fault.
        const string csv = """
        0.000000,0.000000
        0.041708,0.041708
        0.041708,0.041708
        0.083417,0.083417
        """;

        var integrity = PacketTimestampParser.Parse(csv);

        Assert.Equal(0, integrity.NonMonotonicCount);
    }

    [Fact]
    public void Reordered_presentation_times_are_not_a_decode_regression()
    {
        // With B-frames the PTS column re-orders while DTS stays monotonic; only DTS
        // monotonicity is judged, and the latest PTS is tracked for the tail check.
        const string csv = """
        0.000000,0.000000
        0.125125,0.041708
        0.041708,0.083417
        0.083417,0.125125
        """;

        var integrity = PacketTimestampParser.Parse(csv);

        Assert.Equal(0, integrity.NonMonotonicCount);
        Assert.Equal(0.125125, integrity.LastPresentationSeconds);
    }

    [Fact]
    public void A_backward_decode_step_is_counted_and_described()
    {
        const string csv = """
        0.000000,0.000000
        0.083417,0.083417
        0.041708,0.041708
        0.125125,0.125125
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
        0.000000,0.000000
        0.083417,0.083417
        0.041708,0.041708
        0.200000,0.200000
        0.150000,0.150000
        """;

        var integrity = PacketTimestampParser.Parse(csv);

        Assert.Equal(2, integrity.NonMonotonicCount);
        Assert.Contains("0.041708", integrity.FirstRegressionDetail);
    }

    [Fact]
    public void Missing_decode_timestamps_are_skipped_without_breaking_monotonicity()
    {
        // An "N/A" DTS carries no decode order; it must not read as a jump back to zero,
        // and a present PTS on that packet still counts toward the latest presentation.
        const string csv = """
        0.000000,0.000000
        0.041708,0.041708
        0.083417,N/A
        0.125125,0.083417
        """;

        var integrity = PacketTimestampParser.Parse(csv);

        Assert.Equal(0, integrity.NonMonotonicCount);
        Assert.Equal(0.125125, integrity.LastPresentationSeconds);
    }

    [Fact]
    public void Presentation_only_packets_still_provide_a_measured_timeline()
    {
        const string csv = """
        12.000000,N/A,0.020000
        12.020000,N/A,0.020000
        """;

        var integrity = PacketTimestampParser.Parse(csv);

        Assert.Equal(2, integrity.TimestampCount);
        Assert.Equal(0, integrity.NonMonotonicCount);
        Assert.Equal(12.04, integrity.LastPresentationSeconds);
    }

    [Fact]
    public void Last_presentation_is_the_maximum_pts_not_the_final_line()
    {
        const string csv = """
        0.000000,0.000000
        0.200000,0.083417
        0.100000,0.125125
        """;

        var integrity = PacketTimestampParser.Parse(csv);

        Assert.Equal(0.200000, integrity.LastPresentationSeconds);
    }

    [Fact]
    public void Last_presentation_includes_the_final_packets_duration()
    {
        const string csv = """
        0.000000,0.000000,0.041708
        0.041708,0.041708,0.041708
        """;

        var integrity = PacketTimestampParser.Parse(csv);

        Assert.Equal(0.083416, integrity.LastPresentationSeconds);
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
        Assert.Null(integrity.LastPresentationSeconds);
    }

    [Fact]
    public async Task Packet_stream_can_be_measured_incrementally_without_buffering_the_whole_file()
    {
        using var stream = new GeneratedPacketReader(100_000);
        var integrity = await PacketTimestampParser.ParseAsync(stream, CancellationToken.None);

        Assert.Equal(100_000, integrity.TimestampCount);
        Assert.Equal(0, integrity.NonMonotonicCount);
        Assert.Equal(4000.0, integrity.LastPresentationSeconds);
        Assert.Equal(100_000, stream.LinesRead);
    }

    [Fact]
    public void Incremental_scan_preserves_decode_order_across_read_boundaries()
    {
        var scan = new PacketTimestampAccumulator();
        scan.AddLine("0.000000,0.000000,0.040000");
        scan.AddLine("0.120000,0.040000,0.040000");
        scan.AddLine("0.040000,0.080000,0.040000");
        scan.AddLine("0.080000,0.060000,0.040000");

        var integrity = scan.Result;
        Assert.Equal(4, integrity.TimestampCount);
        Assert.Equal(1, integrity.NonMonotonicCount);
        Assert.Equal(0.16, integrity.LastPresentationSeconds);
        Assert.Contains("0.08", integrity.FirstRegressionDetail);
    }

    [Fact]
    public async Task Packet_stream_stops_reading_when_cancelled()
    {
        using var stream = new GeneratedPacketReader(100_000);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await PacketTimestampParser.ParseAsync(stream, cancellation.Token));
        Assert.Equal(0, stream.LinesRead);
    }

    private sealed class GeneratedPacketReader(int count) : StringReader(string.Empty)
    {
        public int LinesRead { get; private set; }

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (LinesRead >= count) return ValueTask.FromResult<string?>(null);
            var seconds = LinesRead++ * 0.04;
            var line = FormattableString.Invariant($"{seconds:0.000000},{seconds:0.000000},0.040000");
            return ValueTask.FromResult<string?>(line);
        }
    }
}
