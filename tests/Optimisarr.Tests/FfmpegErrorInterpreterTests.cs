using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class FfmpegErrorInterpreterTests
{
    [Fact]
    public void Explains_image_subtitles_into_mp4_failure()
    {
        const string stderr =
            "[sost#0:6/mov_text @ 0x5650] Subtitle encoding currently only possible from text to text or bitmap to bitmap\n"
            + "Error opening output file /work/3/Primer.mp4.\nError opening output files: Invalid argument";

        var message = FfmpegErrorInterpreter.Explain(stderr);

        Assert.NotNull(message);
        Assert.Contains("image-based subtitles", message);
        Assert.Contains("MKV", message);
        Assert.Contains("original was not touched", message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Some other ffmpeg error: No space left on device")]
    public void Returns_null_for_unrecognised_or_empty(string? stderr)
    {
        Assert.Null(FfmpegErrorInterpreter.Explain(stderr));
    }
}
