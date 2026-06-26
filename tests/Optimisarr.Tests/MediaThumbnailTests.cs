using Optimisarr.Core.Library;

namespace Optimisarr.Tests;

public sealed class MediaThumbnailTests
{
    [Fact]
    public void CoverArtArguments_select_only_the_attached_picture_and_pass_the_path_as_a_discrete_arg()
    {
        var args = MediaThumbnail.CoverArtArguments("/data/music/Artist/Album/01 - Track.flac");

        // 0:v minus 0:V isolates the cover (attached picture) from any real video.
        Assert.Equal(["-map", "0:v", "-map", "-0:V"], Slice(args, "-map"));
        Assert.Contains("/data/music/Artist/Album/01 - Track.flac", args);
        Assert.Equal("pipe:1", args[^1]);
        // The path is its own element, never folded into another argument (no shell injection surface).
        Assert.Single(args, a => a.Contains("01 - Track.flac"));
    }

    [Fact]
    public void ImageThumbnailArguments_scale_to_the_requested_height_and_emit_one_jpeg_frame()
    {
        var args = MediaThumbnail.ImageThumbnailArguments("/data/photos/holiday/IMG_1.heic", 240);

        Assert.Contains("/data/photos/holiday/IMG_1.heic", args);
        Assert.Equal("scale=-2:240", ValueAfter(args, "-vf"));
        Assert.Equal("1", ValueAfter(args, "-frames:v"));
        Assert.Equal("mjpeg", ValueAfter(args, "-vcodec"));
        Assert.Equal("pipe:1", args[^1]);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "image/png")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "image/gif")]
    public void DetectImageContentType_recognises_image_signatures(byte[] bytes, string expected)
    {
        Assert.Equal(expected, MediaThumbnail.DetectImageContentType(bytes));
    }

    [Fact]
    public void DetectImageContentType_recognises_webp()
    {
        var webp = new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50 };
        Assert.Equal("image/webp", MediaThumbnail.DetectImageContentType(webp));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x00, 0x01, 0x02, 0x03 })]
    [InlineData(new byte[] { 0xFF, 0xD8 })] // truncated, not enough to confirm JPEG
    public void DetectImageContentType_returns_null_for_non_images(byte[] bytes)
    {
        Assert.Null(MediaThumbnail.DetectImageContentType(bytes));
    }

    private static string[] Slice(IReadOnlyList<string> args, string flag)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == flag)
            {
                result.Add(args[i]);
                if (i + 1 < args.Count) result.Add(args[i + 1]);
            }
        }
        return result.ToArray();
    }

    private static string ValueAfter(IReadOnlyList<string> args, string flag)
    {
        var index = args.ToList().IndexOf(flag);
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : string.Empty;
    }
}
