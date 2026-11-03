using Optimisarr.Core.SuperResolution;

namespace Optimisarr.Tests;

/// <summary>
/// Super-resolution re-creates the picture from 8-bit RGB frames, so a source it cannot reproduce
/// faithfully is refused before anything is extracted, with a reason the operator can act on.
/// </summary>
public sealed class SuperResolutionSourceTests
{
    private const string Square8Bit = "\"pix_fmt\": \"yuv420p\", \"sample_aspect_ratio\": \"1:1\", \"start_time\": \"0.000000\"";

    private static string Probe(string extra = "", string video = Square8Bit) => $$"""
        { "streams": [
            { "index": 0, "codec_type": "video", {{video}} },
            { "index": 1, "codec_type": "audio", "start_time": "0.000000" }{{extra}}
          ] }
        """;

    [Fact]
    public void An_ordinary_8_bit_square_pixel_source_is_accepted_and_its_attachments_counted()
    {
        var source = SuperResolutionSource.Parse(Probe(""", { "index": 2, "codec_type": "attachment" }, { "index": 3, "codec_type": "attachment" }"""));
        Assert.Null(source.Refusal);
        Assert.Equal(2, source.AttachmentCount);
        Assert.Equal(0.0, source.VideoStartSeconds);
    }

    [Theory]
    [InlineData("\"pix_fmt\": \"yuv420p10le\", \"sample_aspect_ratio\": \"1:1\"", "8-bit")]
    [InlineData("\"pix_fmt\": \"yuv444p\", \"sample_aspect_ratio\": \"1:1\"", "8-bit")]
    [InlineData("\"pix_fmt\": \"yuv420p\", \"sample_aspect_ratio\": \"32:27\"", "square")]
    [InlineData("\"pix_fmt\": \"yuv420p\", \"side_data_list\": [{ \"side_data_type\": \"Display Matrix\", \"rotation\": -90 }]", "rotat")]
    public void A_source_the_frames_cannot_reproduce_is_refused_with_a_reason(string video, string reason)
    {
        var source = SuperResolutionSource.Parse(Probe(video: video));
        Assert.NotNull(source.Refusal);
        Assert.Contains(reason, source.Refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_second_real_video_stream_is_refused_but_cover_art_is_not()
    {
        var cover = SuperResolutionSource.Parse(Probe(""", { "index": 2, "codec_type": "video", "pix_fmt": "yuvj420p", "disposition": { "attached_pic": 1 } }"""));
        Assert.Null(cover.Refusal);
        var two = SuperResolutionSource.Parse(Probe(""", { "index": 2, "codec_type": "video", "pix_fmt": "yuv420p" }"""));
        Assert.Contains("video stream", two.Refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_late_starting_picture_keeps_its_start_so_audio_stays_in_sync()
    {
        var source = SuperResolutionSource.Parse(Probe(video: Square8Bit.Replace("0.000000", "0.042000")));
        Assert.Null(source.Refusal);
        Assert.Equal(0.042, source.VideoStartSeconds, 6);
    }

    [Fact]
    public void A_probe_without_a_video_stream_is_refused()
    {
        var source = SuperResolutionSource.Parse("""{ "streams": [ { "index": 0, "codec_type": "audio" } ] }""");
        Assert.NotNull(source.Refusal);
    }
}
