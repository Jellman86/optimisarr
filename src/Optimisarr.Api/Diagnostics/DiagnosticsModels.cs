using Optimisarr.Api.Queue;
using Optimisarr.Api.Stats;
using Optimisarr.Core.Tools;
using Optimisarr.Data;

namespace Optimisarr.Api.Diagnostics;

/// <summary>
/// An admin-only support snapshot. Assembled only from non-secret data — counts, types, and flags —
/// so provider tokens, API keys, and webhook URLs (which can embed a secret) are never included. It
/// answers "what is this instance and why are jobs failing?" so an issue can be filed from API
/// evidence alone.
/// </summary>
internal sealed record DiagnosticsBundle(
    string Service,
    string? Version,
    DateTimeOffset GeneratedAt,
    DiagnosticsEnvironment Environment,
    SettingsDto Settings,
    StatsDto Stats,
    IReadOnlyList<ToolCheckResult> Tools,
    HardwareCapabilityResult Hardware,
    IReadOnlyList<DiagnosticsLibrary> Libraries,
    DiagnosticsIntegrations Integrations,
    IReadOnlyList<FailureGroupDto> Failures,
    IReadOnlyList<DiagnosticsLog> RecentLogs);

/// <summary>A captured ffmpeg log for a recently failed job — its non-secret stderr only.</summary>
internal sealed record DiagnosticsLog(int JobId, string? RelativePath, string Log);

internal sealed record DiagnosticsEnvironment(
    string OperatingSystem,
    string Framework,
    string ConfigPath,
    bool ConfigWritable);

internal sealed record DiagnosticsLibrary(
    int Id, string Name, string MediaType, string RuleProfile, bool Enabled, int FileCount);

internal sealed record DiagnosticsIntegrations(
    IReadOnlyList<DiagnosticsWatcher> ActivityWatchers,
    IReadOnlyList<DiagnosticsNotification> NotificationTargets,
    IReadOnlyList<DiagnosticsArr> ArrConnections);

// These summary records deliberately have no field for ApiToken/Token/ApiKey/Url/BaseUrl, so a
// secret cannot leak into the bundle even if a future field is added to the source entity.
internal sealed record DiagnosticsWatcher(string Name, string Type, bool Enabled, bool RefreshOnReplace);
internal sealed record DiagnosticsNotification(
    string Name, string Type, bool Enabled, bool NotifyOnReplacement, bool NotifyOnFailure);
internal sealed record DiagnosticsArr(string Name, string Type, bool Enabled);

/// <summary>
/// Pure mapping of secret-bearing integration entities to their safe diagnostics summaries. The only
/// place integrations enter the bundle, so it is the single point to keep secret-free; unit tested.
/// </summary>
internal static class DiagnosticsRedaction
{
    public static DiagnosticsIntegrations Summarise(
        IEnumerable<ActivityWatcher> watchers,
        IEnumerable<NotificationTarget> targets,
        IEnumerable<ArrConnection> connections) =>
        new(
            watchers
                .Select(w => new DiagnosticsWatcher(w.Name, w.Type.ToString(), w.Enabled, w.RefreshOnReplace))
                .ToList(),
            targets
                .Select(t => new DiagnosticsNotification(
                    t.Name, t.Type.ToString(), t.Enabled, t.NotifyOnReplacement, t.NotifyOnFailure))
                .ToList(),
            connections
                .Select(c => new DiagnosticsArr(c.Name, c.Type.ToString(), c.Enabled))
                .ToList());
}
