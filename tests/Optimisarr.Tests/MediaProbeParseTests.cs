using Optimisarr.Core.Library;

namespace Optimisarr.Tests;

public sealed class MediaProbeParseTests
{
    private const string SampleJson = """
    {
      "streams": [
        { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080 },
        { "codec_type": "audio", "codec_name": "eac3" },
        { "codec_type": "audio", "codec_name": "aac" },
        { "codec_type": "subtitle", "codec_name": "subrip" }
      ],
      "format": { "format_name": "matroska,webm", "duration": "5400.000000" }
    }
    """;

    [Fact]
    public void Parse_extracts_format_video_audio_and_subtitle_details()
    {
        var result = MediaProbeService.Parse(SampleJson);

        Assert.True(result.Success);
        Assert.Equal("matroska,webm", result.Container);
        Assert.Equal(5400.0, result.DurationSeconds);
        Assert.Equal("h264", result.VideoCodec);
        Assert.Equal(1920, result.Width);
        Assert.Equal(1080, result.Height);
        Assert.Equal(new[] { "eac3", "aac" }, result.AudioCodecs);
        Assert.Equal(2, result.AudioTrackCount);
        Assert.Equal(1, result.SubtitleTrackCount);
    }

    [Fact]
    public void Parse_tolerates_missing_optional_fields()
    {
        var result = MediaProbeService.Parse("""{ "streams": [], "format": {} }""");

        Assert.True(result.Success);
        Assert.Null(result.Container);
        Assert.Null(result.DurationSeconds);
        Assert.Null(result.VideoCodec);
        Assert.Empty(result.AudioCodecs);
        Assert.Equal(0, result.SubtitleTrackCount);
    }
}
