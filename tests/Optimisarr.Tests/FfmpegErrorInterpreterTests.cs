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

    [Fact]
    public void Explains_a_rejected_encoder_preset_as_configuration_not_media_failure()
    {
        const string stderr =
            "[hevc_nvenc @ 0x123] Unable to parse option value \"veryslow\"\n"
            + "[hevc_nvenc @ 0x123] Error setting option preset to value veryslow.\n"
            + "Error opening encoder - maybe incorrect parameters such as bit_rate, rate, width or height.";

        var message = FfmpegErrorInterpreter.Explain(stderr);

        Assert.NotNull(message);
        Assert.Contains("Invalid encoder effort", message);
        Assert.Contains("library", message, StringComparison.OrdinalIgnoreCase);
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
