namespace Optimisarr.Api.Replacement;

/// <summary>Resolves the quarantine root consistently for replacement work and setup diagnostics.</summary>
public static class TrashPaths
{
    public static string Resolve(IHostEnvironment environment)
    {
        var configured = Environment.GetEnvironmentVariable("OPTIMISARR_TRASH_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return Directory.Exists("/trash")
            ? "/trash"
            : Path.Combine(environment.ContentRootPath, "trash");
    }
}
