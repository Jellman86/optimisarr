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
    [InlineData("yuv420p", 8)]
    [InlineData("yuvj420p", 8)]
    [InlineData("yuv420p10le", 10)]
    public void Eight_and_ten_bit_4_2_0_sources_are_accepted_and_keep_their_bit_depth(string pixelFormat, int bitDepth)
    {
        var source = SuperResolutionSource.Parse(Probe(video: $"\"pix_fmt\": \"{pixelFormat}\", \"sample_aspect_ratio\": \"1:1\""));
        Assert.Null(source.Refusal);
        Assert.Equal(bitDepth, source.BitDepth);
    }

    [Theory]
    [InlineData("\"pix_fmt\": \"yuv420p12le\", \"sample_aspect_ratio\": \"1:1\"", "4:2:0")]
    [InlineData("\"pix_fmt\": \"yuv444p\", \"sample_aspect_ratio\": \"1:1\"", "4:2:0")]
    [InlineData("\"pix_fmt\": \"yuv422p10le\", \"sample_aspect_ratio\": \"1:1\"", "4:2:0")]
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

    [Fact]
    public void The_picture_is_offset_from_the_files_start_not_from_zero()
    {
        // FFmpeg starts each input at its own earliest timestamp, so a file whose streams all begin
        // at 5 seconds needs no offset; only the gap between the video and the file's start counts.
        var aligned = SuperResolutionSource.Parse("""
            { "format": { "start_time": "5.000000" }, "streams": [
              { "index": 0, "codec_type": "video", "pix_fmt": "yuv420p", "start_time": "5.000000" },
              { "index": 1, "codec_type": "audio", "start_time": "5.000000" } ] }
            """);
        Assert.Equal(0, aligned.VideoStartSeconds, 6);
        var late = SuperResolutionSource.Parse("""
            { "format": { "start_time": "5.000000" }, "streams": [
              { "index": 0, "codec_type": "video", "pix_fmt": "yuv420p", "start_time": "5.042000" },
              { "index": 1, "codec_type": "audio", "start_time": "5.000000" } ] }
            """);
        Assert.Equal(0.042, late.VideoStartSeconds, 6);
    }

    [Fact]
    public void Tagged_colour_is_carried_and_untagged_colour_follows_the_picture_size()
    {
        var tagged = SuperResolutionSource.Parse(Probe(video: Square8Bit + ", \"color_space\": \"bt709\", \"color_range\": \"tv\", \"color_primaries\": \"bt709\", \"color_transfer\": \"bt709\", \"height\": 576"));
        Assert.Equal(new SuperResolutionColour("bt709", "tv", "bt709", "bt709", "bt709", RangeTagged: true), tagged.Colour);

        var hd = SuperResolutionSource.Parse(Probe(video: Square8Bit + ", \"height\": 1080"));
        Assert.Equal(new SuperResolutionColour("bt709", "tv", null, null, null), hd.Colour);
        var sd = SuperResolutionSource.Parse(Probe(video: Square8Bit + ", \"height\": 480"));
        Assert.Equal(new SuperResolutionColour("bt601", "tv", null, null, null), sd.Colour);
        var pal = SuperResolutionSource.Parse(Probe(video: Square8Bit + ", \"color_space\": \"bt470bg\", \"height\": 576"));
        var colour = Assert.IsType<SuperResolutionColour>(pal.Colour);
        Assert.Equal("bt601", colour.Matrix);
        Assert.Equal("bt470bg", colour.Space);
    }

    [Fact]
    public void Fresh_source_facts_supply_frame_rate_without_a_vmaf_probe()
    {
        var source = SuperResolutionSource.Parse(Probe(video: Square8Bit + ", \"avg_frame_rate\": \"24000/1001\", \"r_frame_rate\": \"24000/1001\""));
        Assert.Equal(24000d / 1001, source.FrameRate);
    }

    [Fact]
    public void A_range_tag_without_other_colour_tags_is_preserved()
    {
        var source = SuperResolutionSource.Parse(Probe(video: Square8Bit + ", \"color_range\": \"pc\""));
        var colour = Assert.IsType<SuperResolutionColour>(source.Colour);
        Assert.Equal("pc", colour.Range);
        Assert.True(colour.RangeTagged);
        var jpeg = SuperResolutionSource.Parse(Probe(video: "\"pix_fmt\": \"yuvj420p\""));
        Assert.Equal("pc", Assert.IsType<SuperResolutionColour>(jpeg.Colour).Range);
    }

    [Fact]
    public void Wide_gamut_colour_is_refused()
    {
        var source = SuperResolutionSource.Parse(Probe(video: Square8Bit + ", \"color_space\": \"bt2020nc\""));
        Assert.Contains("colour", source.Refusal, StringComparison.OrdinalIgnoreCase);
    }
}
