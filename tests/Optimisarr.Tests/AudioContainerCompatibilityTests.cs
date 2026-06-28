using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public class AudioContainerCompatibilityTests
{
    [Theory]
    [InlineData("truehd")]
    [InlineData("TrueHD")]
    [InlineData("mlp")]
    [InlineData("pcm_bluray")]
    [InlineData("pcm_dvd")]
    public void Flags_codecs_mp4_cannot_mux(string codec) =>
        Assert.True(AudioContainerCompatibility.IsMp4Incompatible(codec));

    [Theory]
    [InlineData("aac")]
    [InlineData("ac3")]
    [InlineData("eac3")]
    [InlineData("dts")]
    [InlineData("opus")]
    [InlineData("flac")]
    [InlineData(null)]
    [InlineData("")]
    public void Allows_codecs_mp4_can_mux(string? codec) =>
        Assert.False(AudioContainerCompatibility.IsMp4Incompatible(codec));

    [Fact]
    public void Detects_an_incompatible_codec_anywhere_in_an_audio_summary()
    {
        Assert.True(AudioContainerCompatibility.ContainsMp4Incompatible("ac3, truehd"));
        Assert.True(AudioContainerCompatibility.ContainsMp4Incompatible("truehd"));
        Assert.False(AudioContainerCompatibility.ContainsMp4Incompatible("eac3, aac"));
        Assert.False(AudioContainerCompatibility.ContainsMp4Incompatible(null));
    }
}
