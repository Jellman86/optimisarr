using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class SourceTimestampFactsTests
{
    // ffprobe csv=p=1: "packet,codec_type,stream_index,pts,dts" lines, then "stream,index,codec_type".
    private const string VideoAudioSubtitles = "stream,0,video\nstream,1,audio\nstream,2,subtitle\n";

    private static bool Needs(string packets, string streams = VideoAudioSubtitles, bool audioIsCopied = true, bool otherStreamsKept = true) =>
        SourceTimestampFacts.NeedsGeneratedPresentationTimes(packets + streams, audioIsCopied, otherStreamsKept);

    [Fact]
    public void Decode_only_video_with_timed_copied_streams_keeps_the_decoders_own_timing()
    {
        Assert.False(Needs("packet,video,0,N/A,0\npacket,video,0,N/A,40\npacket,audio,1,81,81\npacket,subtitle,2,500,500\n"));
    }

    [Fact]
    public void Video_that_already_carries_presentation_times_keeps_the_existing_regeneration()
    {
        Assert.True(Needs("packet,video,0,0,0\npacket,video,0,80,40\npacket,audio,1,0,0\npacket,subtitle,2,0,0\n"));
    }

    [Fact]
    public void A_copied_stream_without_its_own_presentation_times_still_needs_regeneration()
    {
        const string untimedAudio = "packet,video,0,N/A,0\npacket,audio,1,431,431\npacket,audio,1,N/A,N/A\npacket,subtitle,2,0,0\n";
        Assert.True(Needs(untimedAudio));
        // Re-encoded audio is timed by its decoder, so only copied audio matters.
        Assert.False(Needs(untimedAudio, audioIsCopied: false));
        Assert.True(Needs("packet,video,0,N/A,0\npacket,audio,1,0,0\npacket,subtitle,2,N/A,N/A\n", audioIsCopied: false));
    }

    [Fact]
    public void A_copied_stream_absent_from_the_sample_has_no_timing_evidence_and_keeps_regeneration()
    {
        // The subtitle track starts after the sampled head; its timing is unknown, not proven.
        Assert.True(Needs("packet,video,0,N/A,0\npacket,video,0,N/A,40\npacket,audio,1,81,81\n"));
        Assert.False(Needs("packet,video,0,N/A,0\npacket,video,0,N/A,40\n", audioIsCopied: false,
            streams: "stream,0,video\nstream,1,audio\n"));
    }

    [Fact]
    public void The_first_video_stream_is_the_encoded_one_and_a_timed_copied_cover_picture_is_fine()
    {
        Assert.False(Needs("packet,video,1,0,0\npacket,video,0,N/A,0\npacket,video,0,N/A,40\n",
            streams: "stream,0,video\nstream,1,video\n"));
    }

    [Fact]
    public void A_copied_second_video_track_without_its_own_times_still_needs_regeneration()
    {
        Assert.True(Needs("packet,video,0,N/A,0\npacket,video,1,N/A,0\npacket,video,0,N/A,40\n",
            streams: "stream,0,video\nstream,1,video\n"));
        // A later-starting second video track that the sample never reached counts too.
        Assert.True(Needs("packet,video,0,N/A,0\npacket,video,0,N/A,40\n", streams: "stream,0,video\nstream,1,video\n"));
    }

    [Fact]
    public void When_only_the_encoded_video_is_kept_the_other_streams_do_not_matter()
    {
        Assert.False(Needs("packet,video,0,N/A,0\npacket,video,1,N/A,0\npacket,audio,2,N/A,N/A\n",
            streams: "stream,0,video\nstream,1,video\nstream,2,audio\n", otherStreamsKept: false));
    }

    [Fact]
    public void Attachments_carry_no_packets_and_are_not_held_against_the_source()
    {
        Assert.False(Needs("packet,video,0,N/A,0\npacket,video,0,N/A,40\n", streams: "stream,0,video\nstream,1,attachment\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("packet,audio,0,0,0\nstream,0,audio\n")]
    [InlineData("packet,video,0,N/A,N/A\nstream,0,video\n")]
    [InlineData("packet,video,0,N/A,0\n")]
    [InlineData("garbage\nstream,0,video\n")]
    public void Anything_unclear_keeps_the_existing_regeneration(string output) =>
        Assert.True(SourceTimestampFacts.NeedsGeneratedPresentationTimes(output, audioIsCopied: true));

    [Fact]
    public void Head_read_is_bounded_and_reads_the_demuxers_own_timestamps_and_the_stream_inventory()
    {
        var args = SourceTimestampFacts.Arguments("source with spaces");
        Assert.Contains("%+#256", args);
        Assert.DoesNotContain("+genpts", args);
        Assert.Contains("stream=index,codec_type:packet=codec_type,stream_index,pts,dts", args);
        Assert.Contains("csv=p=1", args);
        Assert.Equal("source with spaces", args[^1]);
    }
}
