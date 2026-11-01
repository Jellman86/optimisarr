using Optimisarr.Core.Library;

namespace Optimisarr.Tests;

public sealed class MediaProbeArgumentsTests
{
    [Fact]
    public void Probe_arguments_bind_demuxing_to_local_single_file_media()
    {
        var args = MediaProbeService.ProbeArguments("movie.mkv");

        var protocol = args.SkipWhile(a => a != "-protocol_whitelist").Skip(1).FirstOrDefault();
        Assert.Equal("file", protocol);
        var formats = args.SkipWhile(a => a != "-format_whitelist").Skip(1).FirstOrDefault();
        Assert.NotNull(formats);
        var allowed = formats!.Split(',');
        // Every inventoried container family stays probeable.
        foreach (var expected in new[] { "matroska", "mov", "avi", "asf", "mpegts", "flv", "mpeg",
                     "wav", "flac", "mp3", "aac", "ogg", "aiff", "ape", "wv", "dsf",
                     "image2", "png_pipe", "webp_pipe", "gif", "bmp_pipe", "tiff_pipe", "jpegxl_pipe" })
            Assert.Contains(expected, allowed);
        // Indirect formats reference media outside the hashed original and must refuse.
        foreach (var refused in new[] { "dash", "hls", "concat", "live_flv", "subfile", "mmsh", "rtsp" })
            Assert.DoesNotContain(refused, allowed);
        Assert.DoesNotContain("-show_data", args);
        Assert.Equal("movie.mkv", args[^1]);
    }
}
