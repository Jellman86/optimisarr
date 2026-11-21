using System.Globalization;

namespace Optimisarr.Core.SuperResolution;

/// <summary>Real-CUGAN through realcugan-ncnn-vulkan, trained for anime and other drawn content.</summary>
public sealed class RealCuganEngine : ISuperResolutionEngine
{
    public static RealCuganEngine Instance { get; } = new();

    private RealCuganEngine() { }

    public string Name => "realcugan";
    public string BinaryName => "realcugan-ncnn-vulkan";
    public bool SupportsCpu => true;
    public IReadOnlyList<string> Presets => RealCuganModelCatalog.Presets;
    public IReadOnlyList<int> Scales { get; } = [2, 3, 4];

    public string? ModelDirectory(string preset) => RealCuganModelCatalog.ModelDirectoryFor(preset);

    public string? ModelFile(string preset, int scale, int noiseLevel) =>
        RealCuganModelCatalog.ModelFileFor(preset, scale, noiseLevel);

    public IReadOnlyList<SuperResolutionValidationError> ValidateModel(string preset, int scale, int noiseLevel)
    {
        var errors = new List<SuperResolutionValidationError>();
        var directory = RealCuganModelCatalog.ModelDirectoryFor(preset);
        if (directory is null)
        {
            errors.Add(new SuperResolutionValidationError(
                "superResolution.preset",
                $"Unknown content preset '{preset}'. Known presets: {string.Join(", ", Presets)}."));
        }

        if (!Scales.Contains(scale))
        {
            errors.Add(new SuperResolutionValidationError(
                "superResolution.scale",
                $"Scale must be one of {string.Join(", ", Scales)}: the shipped models cover 2x, 3x and 4x only."));
        }

        if (noiseLevel is < -1 or > 3)
        {
            errors.Add(new SuperResolutionValidationError(
                "superResolution.noiseLevel",
                "Noise level must be -1 (conservative), 0 (no denoise) or 1 to 3 (denoise strength)."));
        }

        if (directory is not null && ModelFile(preset, scale, noiseLevel) is null)
        {
            errors.Add(new SuperResolutionValidationError(
                "superResolution.modelUnavailable",
                $"The '{directory}' model set ships no model for scale {scale}x at noise level {noiseLevel} " +
                $"(the scheme would need {RealCuganModelCatalog.SchemeFileName(scale, noiseLevel)}). " +
                "Pick a supported combination of preset, scale and denoise."));
        }

        return errors;
    }

    // The release keeps models-se, models-pro and models-nose directly beside the binary.
    public string ModelsRootBeside(string binaryDirectory) => binaryDirectory;

    /// <summary>
    /// Only flags the realcugan-ncnn-vulkan CLI documents are passed: input and output
    /// directories, the model directory selected by the preset, the scale, the noise level, the
    /// device, and the PNG output format. Tuning knobs the operator has not configured (tile size,
    /// sync gap, thread ratios, TTA) stay at the engine defaults.
    /// </summary>
    public IReadOnlyList<string> BuildArguments(string inputDirectory, string outputDirectory, string modelsRoot,
        SuperResolutionOptions options)
    {
        var args = new List<string>
        {
            "-i", inputDirectory,
            "-o", outputDirectory,
            "-m", Path.Combine(modelsRoot, ModelDirectory(options.Preset)!),
            "-s", options.Scale.ToString(CultureInfo.InvariantCulture),
            "-n", options.NoiseLevel.ToString(CultureInfo.InvariantCulture),
        };
        if (options.Device == "cpu")
        {
            // The engine's documented CPU fallback: gpu id -1.
            args.AddRange(["-g", "-1"]);
        }
        else if (options.GpuIndex is { } gpu)
        {
            args.AddRange(["-g", gpu.ToString(CultureInfo.InvariantCulture)]);
        }
        // "auto" passes nothing: the engine's own default picks the Vulkan device.

        args.AddRange(["-f", "png"]);
        return args;
    }
}
