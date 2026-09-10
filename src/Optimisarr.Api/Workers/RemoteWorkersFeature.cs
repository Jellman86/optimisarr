namespace Optimisarr.Api.Workers;

/// <summary>
/// Remote transcoding workers are groundwork in this release, not a feature a user can rely on:
/// the macOS sidecar has done one end-to-end encode on a developer's machine, and worker-side
/// quality measurement, drain controls, credential binding, resumable transfers, signing, and the
/// Windows sidecar are still to build. Until that list is done the whole surface stays out of
/// sight unless an operator opts into the preview with an environment variable, so a default
/// install never meets a switch that leads somewhere unfinished.
/// </summary>
public sealed record RemoteWorkersFeature(bool Available)
{
    public const string EnvironmentVariable = "OPTIMISARR_EXPERIMENTAL_REMOTE_WORKERS";

    public static RemoteWorkersFeature FromEnvironment() =>
        new(IsEnabled(Environment.GetEnvironmentVariable(EnvironmentVariable)));

    /// <summary>Accepts the spellings people actually type into a compose file.</summary>
    public static bool IsEnabled(string? value)
    {
        var trimmed = value?.Trim();
        return trimmed is not null
            && (trimmed == "1"
                || trimmed.Equals("true", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("on", StringComparison.OrdinalIgnoreCase));
    }
}
