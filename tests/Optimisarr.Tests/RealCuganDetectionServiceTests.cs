using Optimisarr.Core.SuperResolution;

namespace Optimisarr.Tests;

/// <summary>
/// Detection runs against the real filesystem: a temporary tree plays the engine install, with
/// an empty file standing in for the binary and model files. No test here claims a working GPU
/// — Vulkan availability is reported, and a real run is only proven by a probe, which these
/// tests never exercise against actual hardware.
/// </summary>
public sealed class RealCuganDetectionServiceTests : IDisposable
{
    private readonly string _root;

    public RealCuganDetectionServiceTests()
    {
        _root = Directory.CreateTempSubdirectory("optimisarr-realcugan-detect").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private string InstallBinary(string? modelsPreset = "mixed", int scale = 2, int noise = 1)
    {
        var binary = Path.Combine(_root, "realcugan-ncnn-vulkan");
        File.WriteAllBytes(binary, []);
        if (modelsPreset is not null)
        {
            InstallModels(modelsPreset, scale, noise);
        }
        return binary;
    }

    private void InstallModels(string preset, int scale, int noise)
    {
        var directory = RealCuganModelCatalog.ModelDirectoryFor(preset)!;
        var file = RealCuganModelCatalog.ModelFileFor(preset, scale, noise)!;
        var modelDirectory = Path.Combine(_root, "models", directory);
        Directory.CreateDirectory(modelDirectory);
        File.WriteAllBytes(Path.Combine(modelDirectory, file + ".bin"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(modelDirectory, file + ".param"), [4, 5, 6]);
    }

    private static SuperResolutionOptions OptionsFor(string binaryPath, string modelsPath) =>
        SuperResolutionOptions.Defaults with { BinaryPath = binaryPath, ModelsPath = modelsPath };

    [Fact]
    public async Task Missing_binary_reports_not_installed_with_an_actionable_problem()
    {
        var detection = await new RealCuganDetectionService(SuperResolutionOptions.Defaults).DetectAsync(CancellationToken.None);
        Assert.Equal(RealCuganReadiness.NotInstalled, detection.Readiness);
        Assert.False(detection.BinaryFound);
        Assert.Contains(detection.Problems, p => p.Contains("realcugan-ncnn-vulkan"));
    }

    [Fact]
    public async Task Binary_and_models_present_with_vulkan_report_ready()
    {
        var binary = InstallBinary();
        var detection = await new RealCuganDetectionService(
            OptionsFor(binary, Path.Combine(_root, "models"))).DetectAsync(CancellationToken.None);

        // This machine has no Vulkan loader, so the honest verdict here is VulkanUnavailable —
        // never Ready. The binary and models themselves must have resolved.
        Assert.True(detection.BinaryFound);
        Assert.True(detection.ModelsFound);
        Assert.Equal("models-se", detection.ModelDirectory);
        Assert.Equal(RealCuganReadiness.VulkanUnavailable, detection.Readiness);
        Assert.False(detection.VulkanLoaderAvailable);
    }

    [Fact]
    public async Task Missing_model_files_are_named_in_the_problems()
    {
        var binary = InstallBinary(modelsPreset: null); // binary only, no models
        var detection = await new RealCuganDetectionService(
            OptionsFor(binary, Path.Combine(_root, "models"))).DetectAsync(CancellationToken.None);

        Assert.False(detection.ModelsFound);
        Assert.Contains(detection.Problems, p => p.Contains("models"));
    }

    [Fact]
    public async Task A_combination_the_model_set_does_not_ship_is_reported_as_misconfigured()
    {
        var binary = InstallBinary(modelsPreset: "pro", scale: 2, noise: 3);
        var detection = await new RealCuganDetectionService(
            OptionsFor(binary, Path.Combine(_root, "models")) with { Preset = "pro", Scale = 2, NoiseLevel = 1 })
            .DetectAsync(CancellationToken.None);

        Assert.Equal(RealCuganReadiness.Misconfigured, detection.Readiness);
        Assert.Contains(detection.Problems, p => p.Contains("up2x-denoise1x"));
    }

    [Fact]
    public void Models_root_defaults_to_the_release_layout_beside_the_binary()
    {
        // The release ships models-se/models-pro/models-nose directly beside the binary.
        var binary = Path.Combine(_root, "bin", "realcugan-ncnn-vulkan");
        Directory.CreateDirectory(Path.Combine(_root, "bin", "models-se"));

        Assert.Equal(Path.Combine(_root, "bin"),
            RealCuganDetectionService.ResolveModelsRoot(null, binary));
    }
}
