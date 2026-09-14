using System.Reflection;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>
/// This service's own build, as an operator would read it off an About box and quote in a bug
/// report.
///
/// Reported on pairing and on every check-in. The server records the protocol version the two ends
/// agreed to speak, which says nothing about which build is installed — so an upgrade that never
/// happened, or one that landed on only some machines, looked exactly like a fleet running the
/// current code. This is the field that tells them apart.
/// </summary>
public static class SidecarBuild
{
    public static string Version { get; } =
        Describe(typeof(SidecarBuild).Assembly);

    /// <summary>Split out so the formatting is testable without an assembly to stand in for one.</summary>
    internal static string Describe(Assembly assembly) =>
        Describe(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assembly.GetName().Version?.ToString());

    internal static string Describe(string? informational, string? assemblyVersion)
    {
        // The informational version carries the source revision when the build sets one
        // (`1.2.3+abc1234`), which is what actually distinguishes two builds calling themselves the
        // same version — the case that matters when a locally built and a released binary collide.
        var preferred = Trimmed(informational) ?? Trimmed(assemblyVersion);

        // "unknown" rather than an invented number when there is nothing to read. An operator uses
        // this field to decide whether to trust what a machine is running, so an honest absence is
        // worth more than a plausible fiction.
        return preferred ?? "unknown";
    }

    private static string? Trimmed(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
