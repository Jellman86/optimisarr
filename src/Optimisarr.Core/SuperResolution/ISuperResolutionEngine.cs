namespace Optimisarr.Core.SuperResolution;

/// <summary>
/// One ncnn-vulkan super-resolution engine: its executable, the models it ships and how it is
/// invoked over a directory of pictures. Decoding, batching, reassembly and verification are
/// engine-neutral and live elsewhere; only what genuinely differs between engines is here.
/// </summary>
public interface ISuperResolutionEngine
{
    /// <summary>The stable engine identifier recorded with status and settings.</summary>
    string Name { get; }

    /// <summary>The executable's file name, looked up on PATH when no path is configured.</summary>
    string BinaryName { get; }

    /// <summary>Whether the engine can run without a Vulkan GPU.</summary>
    bool SupportsCpu { get; }

    /// <summary>The model choices an operator can pick from.</summary>
    IReadOnlyList<string> Presets { get; }

    /// <summary>The scales at least one shipped model covers.</summary>
    IReadOnlyList<int> Scales { get; }

    /// <summary>The preset's folder relative to the models root, or null for an unknown preset.</summary>
    string? ModelDirectory(string preset);

    /// <summary>
    /// The model file, without its .param/.bin extension, that the engine loads for this
    /// combination, or null when no shipped model covers it.
    /// </summary>
    string? ModelFile(string preset, int scale, int noiseLevel);

    /// <summary>
    /// Every reason the combination cannot run. An unsupported combination is a configuration
    /// error; it is never silently replaced by one that can.
    /// </summary>
    IReadOnlyList<SuperResolutionValidationError> ValidateModel(string preset, int scale, int noiseLevel);

    /// <summary>Where the release layout keeps the models, given the executable's folder.</summary>
    string ModelsRootBeside(string binaryDirectory);

    /// <summary>The engine invocation over one batch, as a flat argument array.</summary>
    IReadOnlyList<string> BuildArguments(string inputDirectory, string outputDirectory, string modelsRoot,
        SuperResolutionOptions options);
}
