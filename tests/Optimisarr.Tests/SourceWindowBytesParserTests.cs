using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class SourceWindowBytesParserTests
{
    private const string Streams = """
        "streams": [
          { "index": 0, "codec_type": "video", "disposition": { "attached_pic": 1 } },
          { "index": 1, "codec_type": "video", "disposition": { "attached_pic": 0 } },
          { "index": 2, "codec_type": "audio", "disposition": { "attached_pic": 0 } },
          { "index": 3, "codec_type": "audio", "disposition": { "attached_pic": 0 } },
          { "index": 4, "codec_type": "subtitle", "disposition": { "attached_pic": 0 } }
        ]
        """;

    [Fact]
    public void Packets_inside_the_window_are_summed_per_stream()
    {
        var json = $$"""
            {
              "packets": [
                { "stream_index": 1, "pts_time": "99.500000", "size": "9000" },
                { "stream_index": 1, "pts_time": "100.000000", "size": "5000" },
                { "stream_index": 2, "pts_time": "100.010000", "size": "300" },
                { "stream_index": 1, "pts_time": "120.000000", "size": "4000" },
                { "stream_index": 3, "pts_time": "130.000000", "size": "200" },
                { "stream_index": 4, "pts_time": "135.000000", "size": "50" },
                { "stream_index": 1, "pts_time": "140.000000", "size": "7000" }
              ],
              {{Streams}}
            }
            """;

        var streams = SourceWindowBytesParser.Parse(json, 100, 140)!;

        // The keyframe read before the window and the packet at its end are both outside it.
        Assert.Equal(
            [
                new SampledStreamBytes("video", 1, true, 9_000),
                new SampledStreamBytes("audio", 0, false, 300),
                new SampledStreamBytes("audio", 1, false, 200),
                new SampledStreamBytes("subtitle", 0, false, 50)
            ],
            streams);
    }

    [Fact]
    public void Cover_art_is_never_mistaken_for_the_picture()
    {
        var json = $$"""
            {
              "packets": [ { "stream_index": 0, "pts_time": "100.000000", "size": "90000" } ],
              {{Streams}}
            }
            """;

        Assert.Null(SourceWindowBytesParser.Parse(json, 100, 140));
    }

    [Fact]
    public void Decode_time_stands_in_for_a_missing_presentation_time()
    {
        var json = $$"""
            {
              "packets": [ { "stream_index": 1, "pts_time": "N/A", "dts_time": "110.000000", "size": "1234" } ],
              {{Streams}}
            }
            """;

        Assert.Equal(1_234, SourceWindowBytesParser.Parse(json, 100, 140)!.Single().Bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "packets": [], "streams": [] }""")]
    public void Unreadable_output_measures_nothing(string json)
    {
        Assert.Null(SourceWindowBytesParser.Parse(json, 0, 40));
    }

    [Fact]
    public void The_read_ends_at_an_absolute_time_past_the_window()
    {
        // A relative end counts from the keyframe ffprobe seeks back to and stops short.
        Assert.Equal("1430%1472", SourceWindowBytesProbe.ReadInterval(1430, 40));
        Assert.Equal("12.5%54.5", SourceWindowBytesProbe.ReadInterval(12.5, 40));
    }
}
