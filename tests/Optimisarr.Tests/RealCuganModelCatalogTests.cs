using Optimisarr.Core.SuperResolution;

namespace Optimisarr.Tests;

/// <summary>
/// The catalogue must mirror the model files realcugan-ncnn-vulkan actually ships. Every case
/// here is read from the repository's <c>models/</c> tree: the engine loads
/// <c>up{scale}x-{variant}</c> where noise -1 → "conservative", 0 → "no-denoise" and 1..3 →
/// "denoise{level}x" (src/main.cpp). A combination with no shipped file fails inside the
/// engine, so configuration must reject it first.
/// </summary>
public sealed class RealCuganModelCatalogTests
{
    [Fact]
    public void Mixed_preset_maps_to_the_upstream_default_model_directory()
    {
        Assert.Equal("models-se", RealCuganModelCatalog.ModelDirectoryFor("mixed"));
    }

    [Fact]
    public void Pro_and_nose_presets_map_to_their_shipped_directories()
    {
        Assert.Equal("models-pro", RealCuganModelCatalog.ModelDirectoryFor("pro"));
        Assert.Equal("models-nose", RealCuganModelCatalog.ModelDirectoryFor("nose"));
    }

    [Fact]
    public void Unknown_presets_have_no_model_directory()
    {
        Assert.Null(RealCuganModelCatalog.ModelDirectoryFor("anime"));
        Assert.Null(RealCuganModelCatalog.ModelDirectoryFor(""));
    }

    [Fact]
    public void Se_directory_ships_denoise1x_and_denoise2x_only_at_2x()
    {
        // Read from the upstream models/models-se tree: every noise level exists beside
        // up2x, while up3x and up4x carry only conservative, no-denoise and denoise3x —
        // there is no up3x-denoise1x, up3x-denoise2x, up4x-denoise1x or up4x-denoise2x file.
        foreach (var noise in new[] { -1, 0, 1, 2, 3 })
        {
            Assert.True(RealCuganModelCatalog.IsSupported("mixed", 2, noise),
                $"models-se up2x noise {noise} should exist");
        }
        foreach (var scale in new[] { 3, 4 })
        {
            Assert.True(RealCuganModelCatalog.IsSupported("mixed", scale, -1),
                $"models-se up{scale}x conservative should exist");
            Assert.True(RealCuganModelCatalog.IsSupported("mixed", scale, 0),
                $"models-se up{scale}x no-denoise should exist");
            Assert.True(RealCuganModelCatalog.IsSupported("mixed", scale, 3),
                $"models-se up{scale}x denoise3x should exist");
            Assert.False(RealCuganModelCatalog.IsSupported("mixed", scale, 1),
                $"models-se has no up{scale}x-denoise1x file");
            Assert.False(RealCuganModelCatalog.IsSupported("mixed", scale, 2),
                $"models-se has no up{scale}x-denoise2x file");
        }
    }

    [Fact]
    public void Pro_directory_ships_2x_and_3x_with_conservative_no_denoise_and_denoise3x_only()
    {
        foreach (var scale in new[] { 2, 3 })
        {
            foreach (var noise in new[] { -1, 0, 3 })
            {
                Assert.True(RealCuganModelCatalog.IsSupported("pro", scale, noise),
                    $"models-pro up{scale}x noise {noise} should exist");
            }
        }

        // models-pro has no denoise1x/denoise2x files and no 4x models.
        Assert.False(RealCuganModelCatalog.IsSupported("pro", 2, 1));
        Assert.False(RealCuganModelCatalog.IsSupported("pro", 2, 2));
        Assert.False(RealCuganModelCatalog.IsSupported("pro", 4, -1));
    }

    [Fact]
    public void Nose_directory_ships_only_2x_without_denoise()
    {
        Assert.True(RealCuganModelCatalog.IsSupported("nose", 2, 0));
        Assert.False(RealCuganModelCatalog.IsSupported("nose", 2, -1));
        Assert.False(RealCuganModelCatalog.IsSupported("nose", 3, 0));
        Assert.False(RealCuganModelCatalog.IsSupported("nose", 2, 1));
    }

    [Fact]
    public void Model_file_names_follow_the_engine_selection_scheme()
    {
        Assert.Equal("up2x-denoise1x", RealCuganModelCatalog.ModelFileFor("mixed", 2, 1));
        Assert.Equal("up2x-conservative", RealCuganModelCatalog.ModelFileFor("mixed", 2, -1));
        Assert.Equal("up2x-no-denoise", RealCuganModelCatalog.ModelFileFor("mixed", 2, 0));
        Assert.Equal("up3x-conservative", RealCuganModelCatalog.ModelFileFor("pro", 3, -1));
        Assert.Null(RealCuganModelCatalog.ModelFileFor("nose", 2, 3));
    }

    [Fact]
    public void Out_of_range_scales_and_noise_levels_are_unsupported()
    {
        Assert.False(RealCuganModelCatalog.IsSupported("mixed", 1, 1));
        Assert.False(RealCuganModelCatalog.IsSupported("mixed", 5, 1));
        Assert.False(RealCuganModelCatalog.IsSupported("mixed", 2, -2));
        Assert.False(RealCuganModelCatalog.IsSupported("mixed", 2, 4));
    }

    [Fact]
    public void Preset_list_is_the_documented_order()
    {
        Assert.Equal(new[] { "mixed", "pro", "nose" }, RealCuganModelCatalog.Presets);
    }
}
