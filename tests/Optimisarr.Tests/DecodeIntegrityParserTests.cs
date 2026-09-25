using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class DecodeIntegrityParserTests
{
    [Fact]
    public void Clean_decode_has_no_errors()
    {
        var integrity = DecodeIntegrityParser.Parse("");

        Assert.Equal(0, integrity.ErrorCount);
        Assert.Null(integrity.FirstError);
    }

    [Fact]
    public void Counts_every_error_line_and_keeps_the_first()
    {
        const string stderr =
            "[h264 @ 0x55] error while decoding MB 10 20, bytestream -7\n" +
            "[h264 @ 0x55] concealing 400 DC, 400 AC errors\n" +
            "[matroska,webm @ 0x60] Read error\n";

        var integrity = DecodeIntegrityParser.Parse(stderr);

        Assert.Equal(3, integrity.ErrorCount);
        Assert.Contains("error while decoding MB", integrity.FirstError);
    }

    [Fact]
    public void Blank_lines_are_ignored()
    {
        var integrity = DecodeIntegrityParser.Parse("\n\n  \n[h264 @ 0x55] error\n\n");

        Assert.Equal(1, integrity.ErrorCount);
    }

    [Fact]
    public void Muxer_dts_timestamp_warnings_are_not_counted_as_decode_errors()
    {
        // The exact noise a hardware encoder (e.g. hevc_qsv) produces against the null muxer:
        // equal/duplicate DTS the muxer rejects, repeated once per packet. It is not corruption.
        const string stderr =
            "[null @ 0x55] Application provided invalid, non monotonically increasing dts to muxer in stream 0: 2 >= 2\n" +
            "[null @ 0x55] Application provided invalid, non monotonically increasing dts to muxer in stream 0: 5 >= 5\n";

        var integrity = DecodeIntegrityParser.Parse(stderr);

        Assert.Equal(0, integrity.ErrorCount);
        Assert.Null(integrity.FirstError);
    }

    [Fact]
    public void Genuine_decode_errors_still_count_when_mixed_with_muxer_dts_noise()
    {
        const string stderr =
            "[null @ 0x55] Application provided invalid, non monotonically increasing dts to muxer in stream 0: 2 >= 2\n" +
            "[hevc @ 0x55] Could not find ref with POC 12\n" +
            "[null @ 0x55] Application provided invalid, non monotonically increasing dts to muxer in stream 0: 3 >= 3\n" +
            "[hevc @ 0x55] error while decoding MB 3 4\n";

        var integrity = DecodeIntegrityParser.Parse(stderr);

        // Only the two real decode errors count; the first reported is the real one, not muxer noise.
        Assert.Equal(2, integrity.ErrorCount);
        Assert.Contains("Could not find ref", integrity.FirstError);
    }

    [Fact]
    public void FFmpegs_own_repeat_notice_is_not_a_decode_error()
    {
        // The line that binned twenty-four good encodes. FFmpeg collapses consecutive identical
        // messages into a notice, and the notice was being counted as a corrupt frame — so a file
        // whose only remark was the muxer DTS one we already ignore failed with
        // "1 decode error(s): Last message repeated 1 times".
        var integrity = DecodeIntegrityParser.Parse(
            """
            [null @ 0x1] Application provided invalid, non monotonically increasing dts to muxer
            Last message repeated 1 times
            """);

        Assert.Equal(0, integrity.ErrorCount);
        Assert.Null(integrity.FirstError);
    }

    [Fact]
    public void A_repeat_of_a_real_error_counts_as_the_errors_it_stands_for()
    {
        // The notice exists because FFmpeg stopped printing, not because the frames stopped being
        // corrupt. Three more of them is three more, or a badly damaged file reads as one fault.
        var integrity = DecodeIntegrityParser.Parse(
            """
            [hevc @ 0x1] Could not find ref with POC 42
            Last message repeated 3 times
            """);

        Assert.Equal(4, integrity.ErrorCount);
        Assert.Contains("Could not find ref", integrity.FirstError);
    }

    [Fact]
    public void A_repeat_notice_with_nothing_before_it_is_not_counted()
    {
        // Nothing to stand for. Counting it would invent an error from a line that describes none.
        Assert.Equal(0, DecodeIntegrityParser.Parse("Last message repeated 2 times").ErrorCount);
    }

    [Fact]
    public void A_repeat_following_a_real_error_does_not_resurrect_a_filtered_one()
    {
        // Order matters: the repeat stands for whatever FFmpeg last printed, so a filtered line in
        // between must not let a later notice attach itself to the earlier real error.
        var integrity = DecodeIntegrityParser.Parse(
            """
            [hevc @ 0x1] Could not find ref with POC 42
            [null @ 0x1] Application provided invalid, non monotonically increasing dts to muxer
            Last message repeated 5 times
            """);

        Assert.Equal(1, integrity.ErrorCount);
    }

    [Fact]
    public void Streaming_accumulator_matches_batch_parser_and_can_stop_after_many_real_errors()
    {
        var lines = new[]
        {
            "[null @ 0x1] non monotonically increasing dts to muxer",
            "Last message repeated 30 times",
            "[av1 @ 0x2] Error parsing OBU data",
            "Last message repeated 98 times",
            "[av1 @ 0x2] No sequence header available"
        };
        var accumulator = new DecodeIntegrityAccumulator();
        foreach (var line in lines)
        {
            accumulator.AddLine(line);
        }

        Assert.Equal(DecodeIntegrityParser.Parse(string.Join('\n', lines)), accumulator.Result);
        Assert.True(accumulator.Result.ErrorCount >= DecodeHealthCheck.MaximumUsefulDecodeErrors);
    }
}
