namespace Optimisarr.Api.Workers;

/// <summary>
/// Remote workers are available by default. The existing environment variable remains a
/// deployment-level disable override; pairing and saved settings remain separate controls.
/// </summary>
public sealed record RemoteWorkersFeature(bool Available)
{
    public const string EnvironmentVariable = "OPTIMISARR_EXPERIMENTAL_REMOTE_WORKERS";
    public static string DisabledExplanation =>
        $"Remote workers are disabled for this deployment. Remove the {EnvironmentVariable} override "
        + "or set it to true, then restart.";

    public static RemoteWorkersFeature FromEnvironment() =>
        new(IsEnabled(Environment.GetEnvironmentVariable(EnvironmentVariable)));

    /// <summary>Accepts the spellings people actually type into a compose file.</summary>
    public static bool IsEnabled(string? value)
    {
        var trimmed = value?.Trim();
        return trimmed is null
            || (trimmed == "1"
                || trimmed.Equals("true", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("on", StringComparison.OrdinalIgnoreCase));
    }
}
