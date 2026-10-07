using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class SourceTimestampFactsTests
{
    // codec_type,stream_index,pts,dts — the order ffprobe writes the requested packet fields.
    private const string DecodeOnlyVideoWithTimedAudio = "video,0,N/A,0\nvideo,0,N/A,40\naudio,1,81,81\nvideo,0,N/A,80\n";

    [Fact]
    public void Decode_only_video_with_timed_copied_streams_keeps_the_decoders_own_timing()
    {
        Assert.False(SourceTimestampFacts.NeedsGeneratedPresentationTimes(DecodeOnlyVideoWithTimedAudio, audioIsCopied: true));
    }

    [Fact]
    public void Video_that_already_carries_presentation_times_keeps_the_existing_regeneration()
    {
        Assert.True(SourceTimestampFacts.NeedsGeneratedPresentationTimes("video,0,0,0\nvideo,0,80,40\naudio,1,0,0\n", audioIsCopied: true));
    }

    [Fact]
    public void A_copied_stream_without_its_own_presentation_times_still_needs_regeneration()
    {
        const string untimedAudio = "video,0,N/A,0\naudio,1,431,431\naudio,1,N/A,N/A\nvideo,0,N/A,40\n";
        Assert.True(SourceTimestampFacts.NeedsGeneratedPresentationTimes(untimedAudio, audioIsCopied: true));
        // Re-encoded audio is timed by its decoder, so only copied audio matters.
        Assert.False(SourceTimestampFacts.NeedsGeneratedPresentationTimes(untimedAudio, audioIsCopied: false));
        Assert.True(SourceTimestampFacts.NeedsGeneratedPresentationTimes(
            "video,0,N/A,0\nsubtitle,2,N/A,N/A\n", audioIsCopied: false));
    }

    [Fact]
    public void Only_the_main_video_stream_decides_and_a_cover_picture_does_not_mask_it()
    {
        const string withCover = "video,1,0,0\nvideo,0,N/A,0\nvideo,0,N/A,40\nvideo,0,N/A,80\n";
        Assert.False(SourceTimestampFacts.NeedsGeneratedPresentationTimes(withCover, audioIsCopied: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("audio,0,0,0\n")]
    [InlineData("video,0,N/A,N/A\n")]
    [InlineData("garbage\n")]
    public void Anything_unclear_keeps_the_existing_regeneration(string packets) =>
        Assert.True(SourceTimestampFacts.NeedsGeneratedPresentationTimes(packets, audioIsCopied: true));

    [Fact]
    public void Head_read_is_bounded_and_reads_the_demuxers_own_timestamps()
    {
        var args = SourceTimestampFacts.Arguments("source with spaces");
        Assert.Contains("%+#256", args);
        Assert.DoesNotContain("+genpts", args);
        Assert.Contains("packet=codec_type,stream_index,pts,dts", args);
        Assert.Equal("source with spaces", args[^1]);
    }
}
