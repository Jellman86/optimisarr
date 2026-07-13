using Optimisarr.Core.Tools;

namespace Optimisarr.Tests;

public sealed class MediaToolCommandsTests
{
    [Fact]
    public void Defaults_to_commands_on_path()
    {
        Assert.Equal("ffmpeg", MediaToolCommands.ResolveFfmpeg(null));
        Assert.Equal("ffprobe", MediaToolCommands.ResolveFfprobe(null, null));
    }

    [Fact]
    public void Derives_ffprobe_beside_an_explicit_ffmpeg()
    {
        var ffmpeg = Path.Combine("opt", "media", "ffmpeg");

        Assert.Equal(
            Path.Combine("opt", "media", "ffprobe"),
            MediaToolCommands.ResolveFfprobe(null, ffmpeg));
    }

    [Fact]
    public void Preserves_executable_extension_when_deriving_ffprobe()
    {
        var ffmpeg = Path.Combine("tools", "ffmpeg.exe");

        Assert.Equal(
            Path.Combine("tools", "ffprobe.exe"),
            MediaToolCommands.ResolveFfprobe(null, ffmpeg));
    }

    [Fact]
    public void Explicit_ffprobe_override_wins()
    {
        Assert.Equal(
            "/custom/ffprobe",
            MediaToolCommands.ResolveFfprobe(" /custom/ffprobe ", "/other/ffmpeg"));
    }
}
