namespace Optimisarr.Core.SuperResolution;

/// <summary>The engines the pipeline integrates, by their stable identifier.</summary>
public static class SuperResolutionEngines
{
    public static IReadOnlyList<ISuperResolutionEngine> All { get; } = [RealCuganEngine.Instance, RealEsrganEngine.Instance];

    public static ISuperResolutionEngine? Find(string? name) =>
        All.FirstOrDefault(engine => string.Equals(engine.Name, name, StringComparison.Ordinal));
}

/// <summary>Where this machine keeps an engine. Host-local: never exported or restored.</summary>
public sealed record SuperResolutionEnginePaths(string BinaryPath, string ModelsPath)
{
    public static SuperResolutionEnginePaths Bundled { get; } = new(string.Empty, string.Empty);
}

/// <summary>
/// A library's choice of model. All empty means Real-CUGAN with the server's settings, so a library
/// that has never chosen keeps doing what it did; otherwise engine, model and scale are all named.
/// </summary>
public static class SuperResolutionModelChoice
{
    /// <summary>Why the choice cannot run, or null when it can.</summary>
    public static string? Validate(string? engine, string? model, int? scale)
    {
        if (engine is null)
        {
            return model is null && scale is null
                ? null
                : "A super-resolution model and scale need an engine.";
        }
        if (engine == RealCuganEngine.Instance.Name)
        {
            return "Leave the engine empty to use Real-CUGAN with the server's settings.";
        }
        if (SuperResolutionEngines.Find(engine) is not { } found)
        {
            return $"Unknown super-resolution engine '{engine}'.";
        }
        if (model is null || scale is null)
        {
            return $"Choose a model and a scale for {engine}.";
        }
        return found.ValidateModel(model, scale.Value, 0) is [var first, ..] ? first.Message : null;
    }
}
