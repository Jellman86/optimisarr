namespace Optimisarr.Core.SuperResolution;

/// <summary>
/// The Real-CUGAN model set shipped with realcugan-ncnn-vulkan, and the (scale, noise)
/// combinations each model directory actually contains. Ground truth: the upstream
/// repository's <c>models/</c> tree and its <c>src/main.cpp</c>, which builds the model file
/// name as <c>up{scale}x-{variant}</c> where noise level -1 selects "conservative", 0 selects
/// "no-denoise", and 1..3 selects "denoise{level}x". A combination with no shipped file makes
/// the engine abort at startup, so configuration rejects it first — an unsupported choice must
/// fail loudly, never silently run something else.
/// </summary>
public static class RealCuganModelCatalog
{
    public const string ModelsSe = "models-se";
    public const string ModelsPro = "models-pro";
    public const string ModelsNose = "models-nose";

    /// <summary>
    /// Content presets. A preset names the model directory the engine is pointed at:
    /// "mixed" is the upstream default set (models-se), "pro" the models-pro set, and "nose"
    /// the special models-nose set. The names describe the shipped model directories, not
    /// capabilities the upstream project does not claim.
    /// </summary>
    public const string PresetMixed = "mixed";
    public const string PresetPro = "pro";
    public const string PresetNose = "nose";

    public static IReadOnlyList<string> Presets { get; } = [PresetMixed, PresetPro, PresetNose];

    public static string? ModelDirectoryFor(string preset) => preset switch
    {
        PresetMixed => ModelsSe,
        PresetPro => ModelsPro,
        PresetNose => ModelsNose,
        _ => null,
    };

    /// <summary>Whether the shipped model set contains this combination.</summary>
    public static bool IsSupported(string preset, int scale, int noiseLevel) =>
        ModelFileFor(preset, scale, noiseLevel) is not null;

    /// <summary>
    /// The exact model file (without extension) the engine loads for the combination, or
    /// <c>null</c> when no shipped model covers it.
    /// </summary>
    public static string? ModelFileFor(string preset, int scale, int noiseLevel)
    {
        var variant = VariantFor(preset, scale, noiseLevel);
        return variant is null ? null : $"up{scale}x-{variant}";
    }

    /// <summary>The file name the engine's selection scheme builds for a (scale, noise) pair,
    /// whether or not a shipped model covers it — used to explain rejections.</summary>
    public static string SchemeFileName(int scale, int noiseLevel) =>
        $"up{scale}x-{noiseLevel switch { -1 => "conservative", 0 => "no-denoise", var n => $"denoise{n}x" }}";

    private static string? VariantFor(string preset, int scale, int noiseLevel) =>
        preset switch
        {
            PresetMixed => scale is 2 or 3 or 4 ? VariantForSe(scale, noiseLevel) : null,
            PresetPro => scale is 2 or 3 ? VariantForProNoise(noiseLevel) : null,
            PresetNose => scale == 2 && noiseLevel == 0 ? "no-denoise" : null,
            _ => null,
        };

    // models-se ships denoise1x and denoise2x only beside up2x; up3x and up4x carry
    // the conservative, no-denoise and denoise3x files only (upstream models/ tree).
    private static string? VariantForSe(int scale, int noiseLevel) =>
        scale == 2
            ? VariantForNoise(noiseLevel)
            : noiseLevel is -1 or 0 or 3 ? VariantForNoise(noiseLevel) : null;

    private static string? VariantForNoise(int noiseLevel) => noiseLevel switch
    {
        -1 => "conservative",
        0 => "no-denoise",
        >= 1 and <= 3 => $"denoise{noiseLevel}x",
        _ => null,
    };

    // models-pro ships only the conservative, no-denoise and denoise3x variants.
    private static string? VariantForProNoise(int noiseLevel) => noiseLevel switch
    {
        -1 => "conservative",
        0 => "no-denoise",
        3 => "denoise3x",
        _ => null,
    };
}
