using Optimisarr.Core.Queue;
using Optimisarr.Core.SuperResolution;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class SuperResolutionOptionsTests
{
    [Fact]
    public void Defaults_are_conservative_and_match_the_documented_initial_configuration()
    {
        var defaults = SuperResolutionOptions.Defaults;
        Assert.False(defaults.Enabled);
        Assert.Equal("realcugan", SuperResolutionOptions.EngineName);
        Assert.Equal("mixed", defaults.Preset);
        Assert.Equal(2, defaults.Scale);
        Assert.Equal(1, defaults.NoiseLevel);
        Assert.Equal("auto", defaults.Device);
        Assert.Equal(string.Empty, defaults.BinaryPath);
        Assert.Equal(string.Empty, defaults.ModelsPath);
    }

    [Fact]
    public void Documented_initial_combination_is_valid()
    {
        Assert.Empty(SuperResolutionOptions.Defaults.Validate());
    }

    [Theory]
    [InlineData("anime")]
    [InlineData("")]
    [InlineData("SE")]
    public void Unknown_preset_is_rejected(string preset)
    {
        var options = SuperResolutionOptions.Defaults with { Preset = preset };
        var errors = options.Validate();
        Assert.Contains("superResolution.preset", errors.Select(e => e.Code));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(0)]
    public void Scales_without_shipped_models_are_rejected(int scale)
    {
        var options = SuperResolutionOptions.Defaults with { Scale = scale };
        var errors = options.Validate();
        Assert.Contains("superResolution.scale", errors.Select(e => e.Code));
    }

    [Fact]
    public void Noise_levels_outside_the_engine_range_are_rejected()
    {
        var tooLow = SuperResolutionOptions.Defaults with { NoiseLevel = -2 };
        var tooHigh = SuperResolutionOptions.Defaults with { NoiseLevel = 4 };
        Assert.Contains("superResolution.noiseLevel", tooLow.Validate().Select(e => e.Code));
        Assert.Contains("superResolution.noiseLevel", tooHigh.Validate().Select(e => e.Code));
    }

    [Fact]
    public void Combination_without_a_shipped_model_is_rejected_with_a_clear_error()
    {
        // pro ships no denoise1x model at any scale, and no 4x model at all.
        var proDenoise1 = SuperResolutionOptions.Defaults with { Preset = "pro", Scale = 2, NoiseLevel = 1 };
        var pro4x = SuperResolutionOptions.Defaults with { Preset = "pro", Scale = 4, NoiseLevel = -1 };
        var noseConservative = SuperResolutionOptions.Defaults with { Preset = "nose", NoiseLevel = -1 };

        foreach (var options in new[] { proDenoise1, pro4x, noseConservative })
        {
            var errors = options.Validate();
            Assert.Contains(errors, e => e.Code == "superResolution.modelUnavailable");
        }
    }

    [Fact]
    public void Combination_errors_name_the_missing_model_file()
    {
        var proDenoise1 = SuperResolutionOptions.Defaults with { Preset = "pro", Scale = 2, NoiseLevel = 1 };
        var error = proDenoise1.Validate().Single(e => e.Code == "superResolution.modelUnavailable");
        Assert.Contains("up2x-denoise1x", error.Message);
    }

    [Theory]
    [InlineData("gpu")]
    [InlineData("gpu:x")]
    [InlineData("gpu:-1")]
    [InlineData("automatic")]
    [InlineData("")]
    public void Unknown_devices_are_rejected(string device)
    {
        var options = SuperResolutionOptions.Defaults with { Device = device };
        Assert.Contains("superResolution.device", options.Validate().Select(e => e.Code));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("cpu")]
    [InlineData("gpu:0")]
    [InlineData("gpu:3")]
    public void Known_device_forms_are_accepted(string device)
    {
        var options = SuperResolutionOptions.Defaults with { Device = device };
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void Disabled_options_still_validate_their_values()
    {
        // A saved-but-disabled configuration must not hide an invalid combination: the
        // operator enabling it later would otherwise inherit a broken engine setup.
        var options = SuperResolutionOptions.Defaults with { Enabled = false, Scale = 7 };
        Assert.NotEmpty(options.Validate());
    }

    [Theory]
    [InlineData(2, 640, 360, 1280, 720)]
    [InlineData(3, 640, 360, 1920, 1080)]
    [InlineData(4, 1280, 720, 5120, 2880)]
    public void The_upscaled_size_scales_every_dimension_by_the_engine_ratio(
        int scale, int sourceWidth, int sourceHeight, int expectedWidth, int expectedHeight)
    {
        var options = SuperResolutionOptions.Defaults with { Scale = scale };
        var upscaled = options.UpscaledSize(new PictureSize(sourceWidth, sourceHeight));
        Assert.Equal(new PictureSize(expectedWidth, expectedHeight), upscaled);
    }

    [Fact]
    public void An_unknown_source_size_has_no_upscaled_size_to_promise()
    {
        // Verification is held to the size the job intended. Without the source's dimensions
        // there is no honest expectation to give it, so the pipeline must refuse instead of
        // guessing — and a degenerate zero dimension is as good as unknown.
        Assert.Null(SuperResolutionOptions.Defaults.UpscaledSize(null));
        Assert.Null(SuperResolutionOptions.Defaults.UpscaledSize(new PictureSize(0, 360)));
        Assert.Null(SuperResolutionOptions.Defaults.UpscaledSize(new PictureSize(640, 0)));
    }

    [Fact]
    public void An_upscaled_candidate_is_never_judged_by_the_size_saving_gate()
    {
        // Upscaling is the point of the job: the output is larger than the source by
        // construction, so the gate built to stop encodes that fail to shrink would reject
        // every candidate before its quality was ever judged.
        var policy = SuperResolutionPolicy.ForUpscaledCandidate(
            VerificationPolicy.Default with { RequireSizeReduction = true, MinimumSizeSavingPercent = 25 });
        Assert.False(policy.RequireSizeReduction);
        // Everything else about the policy — the perceptual and stream gates — must survive
        // untouched: a super-resolution candidate is verified like any other candidate.
        Assert.Equal(25, policy.MinimumSizeSavingPercent);
        Assert.Equal(VerificationPolicy.Default.DurationTolerancePercent, policy.DurationTolerancePercent);
    }
}
