using Optimisarr.Api.OpenApi;

namespace Optimisarr.Tests;

public class OptimisarrApiTagsTests
{
    [Theory]
    [InlineData("/api/settings", "Settings")]
    [InlineData("/api/settings/cleanup", "Settings")]
    [InlineData("/api/settings/export", "Settings")]
    [InlineData("/api/queue/status", "Settings")]
    [InlineData("/api/setup", "Setup")]
    [InlineData("/api/libraries", "Libraries")]
    [InlineData("/api/libraries/1/enqueue", "Libraries")]
    [InlineData("/api/exclusions", "Libraries")]
    [InlineData("/api/calibration/00000000-0000-0000-0000-000000000000", "Libraries")]
    [InlineData("/api/media/1/thumbnail", "Inventory")]
    [InlineData("/api/candidates", "Inventory")]
    [InlineData("/api/inventory", "Inventory")]
    [InlineData("/api/preview/1", "Inventory")]
    [InlineData("/api/jobs", "Queue")]
    [InlineData("/api/jobs/1/replace", "Queue")]
    [InlineData("/api/replacements/1/approve", "Replacements")]
    [InlineData("/api/arr-connections", "Integrations")]
    [InlineData("/api/notification-targets", "Integrations")]
    [InlineData("/api/activity-watchers", "Integrations")]
    [InlineData("/api/connect/test", "Integrations")]
    [InlineData("/api/health", "System")]
    [InlineData("/api/diagnostics", "System")]
    [InlineData("/api/system/tools", "System")]
    [InlineData("/hubs/queue", "Realtime")]
    public void TagFor_groups_paths_by_area(string path, string expected) =>
        Assert.Equal(expected, OptimisarrApiTags.TagFor(path));

    [Theory]
    [InlineData("/api/settings/export")]
    [InlineData("/api/settings/cleanup")]
    [InlineData("/api/jobs/1/replace")]
    [InlineData("/api/replacements/1/rollback")]
    [InlineData("/api/libraries/1/enqueue")]
    [InlineData("/api/diagnostics")]
    [InlineData("/api/setup")]
    [InlineData("/api/setup/apply")]
    [InlineData("/api/setup/restart")]
    [InlineData("/api/calibration/00000000-0000-0000-0000-000000000000/apply")]
    public void RequiresAdminToken_is_true_for_protected_endpoints(string path) =>
        Assert.True(OptimisarrApiTags.RequiresAdminToken(path));

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/api/ready")]
    [InlineData("/api/auth/status")]
    public void RequiresAdminToken_is_false_for_open_endpoints(string path) =>
        Assert.False(OptimisarrApiTags.RequiresAdminToken(path));

    [Theory]
    [InlineData("/api/settings")]
    [InlineData("/api/libraries")]
    [InlineData("/api/jobs")]
    [InlineData("/api/replacements/1")]
    [InlineData("/api/arr-connections")]
    [InlineData("/api/health")]
    [InlineData("/hubs/queue")]
    [InlineData("/something/unmapped")]
    public void TagFor_only_returns_declared_tags(string path) =>
        Assert.Contains(OptimisarrApiTags.TagFor(path), OptimisarrApiTags.AllTags);
}
