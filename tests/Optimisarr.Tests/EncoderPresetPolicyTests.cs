using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class EncoderPresetPolicyTests
{
    [Theory]
    [InlineData("libx264", "quick", "fast")]
    [InlineData("libx265", "balanced", "medium")]
    [InlineData("libx265", "efficient", "slow")]
    [InlineData("h264_nvenc", "quick", "p2")]
    [InlineData("hevc_nvenc", "balanced", "p4")]
    [InlineData("av1_nvenc", "efficient", "p7")]
    [InlineData("h264_qsv", "quick", "fast")]
    [InlineData("hevc_qsv", "balanced", "medium")]
    [InlineData("av1_qsv", "efficient", "slow")]
    [InlineData("libsvtav1", "quick", "10")]
    [InlineData("libsvtav1", "balanced", "8")]
    [InlineData("libsvtav1", "efficient", "6")]
    public void Portable_effort_maps_to_a_valid_preset_for_the_resolved_encoder(
        string encoder,
        string effort,
        string expectedPreset)
    {
        var result = EncoderPresetPolicy.Resolve(encoder, effort);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(effort, result.Effort);
        Assert.Equal(expectedPreset, result.FfmpegPreset);
    }

    [Theory]
    [InlineData("h264_vaapi")]
    [InlineData("hevc_vaapi")]
    [InlineData("av1_vaapi")]
    public void Vaapi_uses_its_driver_default_instead_of_receiving_an_invalid_preset(string encoder)
    {
        var result = EncoderPresetPolicy.Resolve(encoder, "efficient");

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("efficient", result.Effort);
        Assert.Null(result.FfmpegPreset);
    }

    [Fact]
    public void Encoder_default_omits_the_preset_for_every_encoder()
    {
        var result = EncoderPresetPolicy.Resolve("hevc_nvenc", null);

        Assert.True(result.Succeeded, result.Error);
        Assert.Null(result.Effort);
        Assert.Null(result.FfmpegPreset);
    }

    [Theory]
    [InlineData("fast", "fast")]
    [InlineData("ultrafast", "ultrafast")]
    [InlineData("medium", "medium")]
    [InlineData("veryslow", "veryslow")]
    [InlineData("P2", "p2")]
    [InlineData("p4", "p4")]
    [InlineData("p7", "p7")]
    [InlineData("13", "13")]
    [InlineData("8", "8")]
    [InlineData("0", "0")]
    public void Legacy_cpu_nvenc_and_svtav1_values_are_recognised_without_losing_their_exact_intent(
        string legacy,
        string expected)
    {
        var valid = EncoderPresetPolicy.TryNormaliseSelection(legacy, out var normalised);

        Assert.True(valid);
        Assert.Equal(expected, normalised);
    }

    [Theory]
    [InlineData("libx264", "ultrafast", "ultrafast")]
    [InlineData("libx265", "veryslow", "veryslow")]
    [InlineData("hevc_nvenc", "ultrafast", "p1")]
    [InlineData("hevc_nvenc", "veryslow", "p7")]
    [InlineData("hevc_nvenc", "p6", "p6")]
    [InlineData("hevc_qsv", "superfast", "veryfast")]
    [InlineData("hevc_qsv", "slower", "slower")]
    [InlineData("libsvtav1", "13", "13")]
    [InlineData("libsvtav1", "p7", "2")]
    [InlineData("libx265", "p7", "veryslow")]
    public void Legacy_values_resolve_safely_while_preserving_exact_values_for_their_native_encoder(
        string encoder,
        string legacy,
        string expectedPreset)
    {
        var result = EncoderPresetPolicy.Resolve(encoder, legacy);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(expectedPreset, result.FfmpegPreset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("-2")]
    [InlineData("-1")]
    public void Empty_or_encoder_default_legacy_values_normalise_to_null(string? value)
    {
        var valid = EncoderPresetPolicy.TryNormaliseSelection(value, out var normalised);

        Assert.True(valid);
        Assert.Null(normalised);
    }

    [Theory]
    [InlineData("not-a-preset")]
    [InlineData("hq")]
    [InlineData("14")]
    public void Unknown_or_non_portable_values_are_rejected(string value)
    {
        Assert.False(EncoderPresetPolicy.TryNormaliseSelection(value, out _));

        var result = EncoderPresetPolicy.Resolve("hevc_nvenc", value);
        Assert.False(result.Succeeded);
        Assert.Contains("Invalid encoder effort", result.Error);
    }

    [Fact]
    public void An_unknown_encoder_fails_closed_when_an_effort_was_selected()
    {
        var result = EncoderPresetPolicy.Resolve("future_encoder", "balanced");

        Assert.False(result.Succeeded);
        Assert.Contains("future_encoder", result.Error);
    }
}
