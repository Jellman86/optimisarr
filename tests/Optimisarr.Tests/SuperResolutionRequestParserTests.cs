using Optimisarr.Api.Library;
using Optimisarr.Core.SuperResolution;

namespace Optimisarr.Tests;

/// <summary>
/// The settings API must reject an unsupported engine configuration with a specific error code
/// instead of storing it — an engine run has to be exactly what the operator configured.
/// </summary>
public sealed class SuperResolutionRequestParserTests
{
    [Fact]
    public void The_documented_initial_combination_parses()
    {
        var ok = SuperResolutionRequestParser.TryParse(
            new SuperResolutionSettingsDto(true, "mixed", 2, 1, "auto", "", ""),
            out var options, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.True(options.Enabled);
        Assert.Equal(SuperResolutionOptions.EngineName, SuperResolutionSettingsDto.Engine);
    }

    [Fact]
    public void An_unsupported_model_combination_is_rejected_with_the_model_error()
    {
        var ok = SuperResolutionRequestParser.TryParse(
            new SuperResolutionSettingsDto(true, "pro", 2, 1, "auto", "", ""),
            out _, out var error);

        Assert.False(ok);
        Assert.Equal("superResolution.modelUnavailable", error?.Code);
    }

    [Fact]
    public void An_unknown_preset_is_rejected_with_the_preset_error()
    {
        var ok = SuperResolutionRequestParser.TryParse(
            new SuperResolutionSettingsDto(true, "anime", 2, 1, "auto", "", ""),
            out _, out var error);

        Assert.False(ok);
        Assert.Equal("superResolution.preset", error?.Code);
    }

    [Fact]
    public void An_unknown_device_is_rejected_with_the_device_error()
    {
        var ok = SuperResolutionRequestParser.TryParse(
            new SuperResolutionSettingsDto(true, "mixed", 2, 1, "cuda:0", "", ""),
            out _, out var error);

        Assert.False(ok);
        Assert.Equal("superResolution.device", error?.Code);
    }
}
