using Optimisarr.Core.Queue;

namespace Optimisarr.Core.SuperResolution;

/// <summary>A single failed validation. <see cref="Code"/> is the API error code.</summary>
public sealed record SuperResolutionValidationError(string Code, string Message);

/// <summary>
/// The Real-CUGAN super-resolution engine configuration. Stored as <c>realcugan.*</c> rows in
/// the settings table. The engine choice itself is fixed — <see cref="EngineName"/> — because
/// this is the only engine the pipeline integrates; the switch that matters is <see cref="Enabled"/>,
/// which stays off until an operator turns it on, so no existing job changes behaviour.
/// </summary>
public sealed record SuperResolutionOptions(
    bool Enabled,
    string Preset,
    int Scale,
    int NoiseLevel,
    string Device,
    string BinaryPath,
    string ModelsPath)
{
    public const string EngineName = "realcugan";

    /// <summary>
    /// The initial configuration. Enabled is deliberately false: a fresh install must not route
    /// video jobs through an engine that has not been proven present, and the preset, scale,
    /// denoise and device values are the documented starting combination.
    /// </summary>
    public static SuperResolutionOptions Defaults { get; } = new(
        Enabled: false,
        Preset: RealCuganModelCatalog.PresetMixed,
        Scale: 2,
        NoiseLevel: 1,
        Device: "auto",
        BinaryPath: string.Empty,
        ModelsPath: string.Empty);

    /// <summary>Valid scales the shipped models cover.</summary>
    public static IReadOnlyList<int> Scales { get; } = [2, 3, 4];

    /// <summary>Whether this engine run targets a Vulkan device rather than the CPU fallback.</summary>
    public bool DeviceIsGpu => Device.StartsWith("gpu:", StringComparison.Ordinal);

    /// <summary>The numeric Vulkan device index for a well-formed "gpu:N" device, or null otherwise.</summary>
    public int? GpuIndex => DeviceIsGpu
        && int.TryParse(Device["gpu:".Length..], out var index)
        && index >= 0
        ? index
        : null;

    /// <summary>
    /// The picture size the engine produces for this source: every frame leaves the engine
    /// scaled by exactly <see cref="Scale"/>, and the reassembly encodes the upscaled frames as
    /// they are. Null when the source's dimensions are unknown — the expected size verification
    /// holds the candidate to cannot be promised without them.
    /// </summary>
    public PictureSize? UpscaledSize(PictureSize? source) =>
        source is { } picture && picture.Width > 0 && picture.Height > 0
            ? new PictureSize(picture.Width * Scale, picture.Height * Scale)
            : null;

    /// <summary>
    /// Validates every field and the combination as a whole. Returns every problem found —
    /// an unsupported combination is a configuration error, never something to silently
    /// substitute. Existence of binaries, models and a Vulkan runtime is checked at detection
    /// time, not here: a saved path may legitimately be unreachable while the container is
    /// restarted, and that must be reported as engine status rather than corrupt the config.
    /// </summary>
    public IReadOnlyList<SuperResolutionValidationError> Validate()
    {
        var errors = new List<SuperResolutionValidationError>();

        if (RealCuganModelCatalog.ModelDirectoryFor(Preset) is null)
        {
            errors.Add(new SuperResolutionValidationError(
                "superResolution.preset",
                $"Unknown content preset '{Preset}'. Known presets: {string.Join(", ", RealCuganModelCatalog.Presets)}."));
        }

        if (!Scales.Contains(Scale))
        {
            errors.Add(new SuperResolutionValidationError(
                "superResolution.scale",
                $"Scale must be one of {string.Join(", ", Scales)}: the shipped models cover 2x, 3x and 4x only."));
        }

        if (NoiseLevel is < -1 or > 3)
        {
            errors.Add(new SuperResolutionValidationError(
                "superResolution.noiseLevel",
                "Noise level must be -1 (conservative), 0 (no denoise) or 1 to 3 (denoise strength)."));
        }

        var expectedModelFile = RealCuganModelCatalog.ModelFileFor(Preset, Scale, NoiseLevel);
        if (RealCuganModelCatalog.ModelDirectoryFor(Preset) is { } directory
            && expectedModelFile is null)
        {
            errors.Add(new SuperResolutionValidationError(
                "superResolution.modelUnavailable",
                $"The '{directory}' model set ships no model for scale {Scale}x at noise level {NoiseLevel} " +
                $"(the scheme would need {RealCuganModelCatalog.SchemeFileName(Scale, NoiseLevel)}). " +
                "Pick a supported combination of preset, scale and denoise."));
        }

        if (Device != "auto" && Device != "cpu" && !DeviceIsGpu)
        {
            errors.Add(new SuperResolutionValidationError(
                "superResolution.device",
                "Device must be 'auto', 'cpu', or 'gpu:<index>' for a detected Vulkan device."));
        }
        else if (DeviceIsGpu && GpuIndex is null or < 0)
        {
            errors.Add(new SuperResolutionValidationError(
                "superResolution.device",
                "A 'gpu:<index>' device needs a non-negative Vulkan device index."));
        }

        return errors;
    }
}
