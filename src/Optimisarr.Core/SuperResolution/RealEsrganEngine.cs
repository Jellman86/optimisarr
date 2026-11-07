using System.Globalization;

namespace Optimisarr.Core.SuperResolution;

/// <summary>
/// Real-ESRGAN through realesrgan-ncnn-vulkan. Its general model suits live action, which Real-CUGAN
/// is not trained for. The two x4 models are fixed networks: asked for a smaller scale, the engine
/// still exits successfully but crops and misstitches the picture, which the dimension gate cannot
/// see, so the catalogue refuses those combinations.
/// </summary>
public sealed class RealEsrganEngine : ISuperResolutionEngine
{
    public const string LiveActionModel = "realesrgan-x4plus";
    public const string DrawnModel = "realesrgan-x4plus-anime";
    public const string DrawnVideoModel = "realesr-animevideov3";

    // The engine rejects any -m path that does not contain "models".
    private const string ModelFolder = "models";

    public static RealEsrganEngine Instance { get; } = new();

    private RealEsrganEngine() { }

    public string Name => "realesrgan";
    public string BinaryName => "realesrgan-ncnn-vulkan";
    public bool SupportsCpu => false;
    public IReadOnlyList<string> Presets { get; } = [DrawnVideoModel, LiveActionModel, DrawnModel];
    public IReadOnlyList<int> Scales { get; } = [2, 3, 4];

    public string? ModelDirectory(string preset) => Presets.Contains(preset) ? ModelFolder : null;

    public string? ModelFile(string preset, int scale, int noiseLevel) => preset switch
    {
        DrawnVideoModel when Scales.Contains(scale) => $"{DrawnVideoModel}-x{scale.ToString(CultureInfo.InvariantCulture)}",
        LiveActionModel or DrawnModel when scale == 4 => preset,
        _ => null,
    };

    public IReadOnlyList<SuperResolutionValidationError> ValidateModel(string preset, int scale, int noiseLevel)
    {
        if (!Presets.Contains(preset))
        {
            return [new SuperResolutionValidationError(
                "superResolution.preset",
                $"Unknown Real-ESRGAN model '{preset}'. Known models: {string.Join(", ", Presets)}.")];
        }
        if (!Scales.Contains(scale))
        {
            return [new SuperResolutionValidationError(
                "superResolution.scale",
                $"Scale must be one of {string.Join(", ", Scales)}: the shipped models cover 2x, 3x and 4x only.")];
        }
        return ModelFile(preset, scale, noiseLevel) is null
            ? [new SuperResolutionValidationError(
                "superResolution.modelUnavailable",
                $"The '{preset}' model only upscales 4x, so {scale}x is not available. Choose 4x, or a model that covers {scale}x.")]
            : [];
    }

    public string ModelsRootBeside(string binaryDirectory) => binaryDirectory;

    /// <summary>Only the documented flags. Unlike Real-CUGAN, -n is the model's name, not a denoise level.</summary>
    public IReadOnlyList<string> BuildArguments(string inputDirectory, string outputDirectory, string modelsRoot,
        SuperResolutionOptions options)
    {
        var args = new List<string>
        {
            "-i", inputDirectory,
            "-o", outputDirectory,
            "-n", options.Preset,
            "-s", options.Scale.ToString(CultureInfo.InvariantCulture),
            "-m", Path.Combine(modelsRoot, ModelFolder),
        };
        if (options.GpuIndex is { } gpu) args.AddRange(["-g", gpu.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["-f", "png"]);
        return args;
    }
}
