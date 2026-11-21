using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

/// <summary>
/// Intel QSV stamps decode times from a fixed frame interval, so a source whose picture times jump
/// (two Pokémon episodes have a 4-second gap at 17:08) makes it emit a decode time earlier than
/// the last one. The muxer refuses that packet and FFmpeg stops. VA-API on the same GPU and the
/// software encoders encode the same frames cleanly, so the job is retried without QSV.
/// </summary>
public sealed class IntelQsvTimestampFallbackTests
{
    private const string MuxerRefusal =
        "[vost#0:0/hevc_qsv @ 0x56268322f080] Non-monotonic DTS; previous: 16507520, current: 16444160; "
        + "Error submitting a packet to the muxer: Invalid argument\n[out#0/mp4 @ 0x56268325b4c0] Error muxing a packet\n"
        + "Conversion failed!";

    [Theory]
    [InlineData("hevc_qsv")]
    [InlineData("h264_qsv")]
    [InlineData("av1_qsv")]
    public void A_qsv_encode_the_muxer_refused_for_backward_decode_times_is_retried_without_qsv(string encoder)
    {
        Assert.True(IntelQsvTimestampFallback.Applies(encoder, MuxerRefusal));
    }

    [Theory]
    [InlineData("hevc_vaapi")]
    [InlineData("libx265")]
    [InlineData(null)]
    public void Another_encoder_with_the_same_error_is_not_blamed_on_qsv(string? encoder)
    {
        Assert.False(IntelQsvTimestampFallback.Applies(encoder, MuxerRefusal));
    }

    [Fact]
    public void A_copied_audio_timestamp_warning_beside_an_unrelated_muxer_failure_is_not_blamed_on_qsv()
    {
        const string log = "[mp4 @ 0x5626] Non-monotonic DTS in output stream 0:1; previous: 1000, current: 990; changing to 1001.\n"
            + "[vost#0:0/hevc_qsv @ 0x5626] Error submitting a packet to the muxer: No space left on device\nConversion failed!";

        Assert.False(IntelQsvTimestampFallback.Applies("hevc_qsv", log));
    }

    [Theory]
    [InlineData("Error while opening encoder for output stream #0:0")]
    [InlineData("No space left on device")]
    [InlineData("")]
    [InlineData(null)]
    public void A_qsv_failure_of_another_kind_is_not_retried_this_way(string? log)
    {
        Assert.False(IntelQsvTimestampFallback.Applies("hevc_qsv", log));
    }
}
