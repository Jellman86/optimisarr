using Optimisarr.Api.Security;

namespace Optimisarr.Api.OpenApi;

/// <summary>
/// Pure mapping from an endpoint's route path to a documentation tag (so the generated OpenAPI groups
/// operations by area) and to whether the operation is admin-token protected. Unit tested.
/// </summary>
internal static class OptimisarrApiTags
{
    public static readonly string[] AllTags =
        ["System", "Setup", "Settings", "Libraries", "Inventory", "Queue", "Replacements", "Integrations", "Realtime"];

    public static string TagFor(string path)
    {
        var p = path.StartsWith('/') ? path : "/" + path;

        if (p.StartsWith("/hubs", StringComparison.OrdinalIgnoreCase)) return "Realtime";
        if (p.StartsWith("/api/setup", StringComparison.OrdinalIgnoreCase)) return "Setup";
        if (p.StartsWith("/api/settings", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/api/queue", StringComparison.OrdinalIgnoreCase)) return "Settings";
        if (p.StartsWith("/api/libraries", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/api/exclusions", StringComparison.OrdinalIgnoreCase)) return "Libraries";
        if (p.StartsWith("/api/media", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/api/candidates", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/api/inventory", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/api/preview", StringComparison.OrdinalIgnoreCase)) return "Inventory";
        if (p.StartsWith("/api/jobs", StringComparison.OrdinalIgnoreCase)) return "Queue";
        if (p.StartsWith("/api/replacements", StringComparison.OrdinalIgnoreCase)) return "Replacements";
        if (p.StartsWith("/api/activity-watchers", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/api/notification-targets", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/api/arr-connections", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("/api/connect", StringComparison.OrdinalIgnoreCase)) return "Integrations";

        // Health, readiness, auth status, system tools/hardware, fs browse, library-options, stats,
        // and diagnostics.
        return "System";
    }

    /// <summary>True when the operation is rejected with 401 if the admin token is configured.</summary>
    public static bool RequiresAdminToken(string path) =>
        AdminTokenAuth.IsProtectedPath(path) && !AdminTokenAuth.IsOpenPath(path);
}
