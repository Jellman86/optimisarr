using Optimisarr.Api.Diagnostics;

namespace Optimisarr.Tests;

public sealed class DiagnosticSafeFieldsTests
{
    [Theory]
    [InlineData("Decode health", "Decode health")]
    [InlineData("Authorization: Bearer secret", "Other verification check")]
    [InlineData("secret-token", "Other verification check")]
    public void Only_known_verification_check_names_enter_bundles(string value, string expected) =>
        Assert.Equal(expected, DiagnosticSafeFields.CheckName(value));

    [Theory]
    [InlineData("hevc_nvenc", "hevc_nvenc")]
    [InlineData("hevc_videotoolbox", "hevc_videotoolbox")]
    [InlineData("-i /data/secret-token", null)]
    public void Encoders_are_whitelisted_rather_than_copied_from_free_text(string value, string? expected) =>
        Assert.Equal(expected, DiagnosticSafeFields.Encoder(value));

    [Theory]
    [InlineData("videotoolbox", "videotoolbox")]
    [InlineData("cuda", "cuda")]
    [InlineData("-hwaccel /private/path", null)]
    public void Decoders_are_whitelisted_rather_than_copied_from_free_text(string value, string? expected) =>
        Assert.Equal(expected, DiagnosticSafeFields.Decoder(value));

    [Theory]
    [InlineData("v0.2.14", "v0.2.14")]
    [InlineData("0.2.14+5b7864b", "0.2.14+5b7864b")]
    [InlineData("1.secret", null)]
    [InlineData("1.2.3+secret", null)]
    [InlineData("Bearer secret-token", null)]
    public void Only_version_shaped_sidecar_versions_enter_bundles(string value, string? expected) =>
        Assert.Equal(expected, DiagnosticSafeFields.Version(value));
}
