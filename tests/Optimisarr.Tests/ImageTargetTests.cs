using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class ImageTargetTests
{
    [Theory]
    [InlineData("png")]
    [InlineData("bmp")]
    [InlineData("tiff")]
    [InlineData("gif")]
    public void Lossless_source_formats_are_recognised(string codec)
    {
        Assert.True(ImageTarget.IsLossless(codec));
    }

    [Theory]
    [InlineData("mjpeg")]
    [InlineData("webp")]
    [InlineData("av1")]
    public void Lossy_or_already_efficient_formats_are_not_lossless(string codec)
    {
        Assert.False(ImageTarget.IsLossless(codec));
    }

    [Theory]
    // A still already in the target format (allowing for ffprobe's codec names) is not worth re-encoding.
    [InlineData("webp", "webp", true)]
    [InlineData("av1", "avif", true)]
    [InlineData("jpegxl", "jxl", true)]
    [InlineData("mjpeg", "webp", false)]
    [InlineData("png", "avif", false)]
    public void IsAlreadyInFormat_maps_ffprobe_codec_names_to_target_formats(
        string sourceCodec, string targetFormat, bool expected)
    {
        Assert.Equal(expected, ImageTarget.IsAlreadyInFormat(sourceCodec, targetFormat));
    }

    [Theory]
    [InlineData("mjpeg", "jpeg", true)]
    [InlineData("jpeg", "jpeg", true)]
    [InlineData("png", "jpeg", false)]
    public void IsAlreadyInFormat_recognises_a_jpeg_source(string sourceCodec, string targetFormat, bool expected)
    {
        Assert.Equal(expected, ImageTarget.IsAlreadyInFormat(sourceCodec, targetFormat));
    }

    [Theory]
    [InlineData("jpeg")]
    [InlineData("webp")]
    [InlineData("avif")]
    [InlineData("jxl")]
    public void Supported_targets_resolve_to_an_encoder_and_extension(string format)
    {
        Assert.True(ImageTarget.IsSupportedTarget(format));

        var spec = ImageTarget.Resolve(format);

        Assert.False(string.IsNullOrWhiteSpace(spec.Encoder));
        Assert.False(string.IsNullOrWhiteSpace(spec.Extension));
    }

    [Fact]
    public void Resolving_an_unsupported_format_throws()
    {
        Assert.False(ImageTarget.IsSupportedTarget("png"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageTarget.Resolve("png"));
    }

    [Fact]
    public void Jpeg_webp_and_avif_are_encodable_targets_but_jxl_is_detect_only()
    {
        // JPEG/WebP/AVIF form the compatibility→efficiency axis offered to operators. JXL is
        // recognised as a *source* (so an already-JXL file is detected) but never an encode target,
        // because no media server displays it.
        Assert.True(ImageTarget.IsEncodable("jpeg"));
        Assert.True(ImageTarget.IsEncodable("webp"));
        Assert.True(ImageTarget.IsEncodable("avif"));
        Assert.False(ImageTarget.IsEncodable("jxl"));
        Assert.Equal(new[] { "jpeg", "webp", "avif" }, ImageTarget.EncodableFormats);
    }

    [Fact]
    public void The_default_format_is_jpeg_for_maximum_compatibility()
    {
        Assert.Equal("jpeg", ImageTarget.DefaultFormat);
        Assert.True(ImageTarget.IsEncodable(ImageTarget.DefaultFormat));
    }
}
