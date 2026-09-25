using System.Globalization;
using System.Text.RegularExpressions;

namespace Optimisarr.Core.Workers;

public enum SidecarUpdateState
{
    /// <summary>The sidecar did not report a version this can read, or the server has none.</summary>
    Unknown,
    /// <summary>The sidecar is on the server's release.</summary>
    Current,
    /// <summary>The sidecar is on an older release than the server.</summary>
    UpdateAvailable,
    /// <summary>The sidecar is ahead of the server. Never a reason to warn.</summary>
    Newer
}

/// <summary>What a worker is told about its own version, and where the matching release is.</summary>
public sealed record SidecarUpdateStatus(SidecarUpdateState State, string? LatestVersion, string? ReleaseUrl)
{
    public static SidecarUpdateStatus Unknown { get; } = new(SidecarUpdateState.Unknown, null, null);
}

/// <summary>
/// Compares a sidecar's reported version with the server's.
///
/// <para>Sidecars do not update themselves, and one left behind can do real harm while looking
/// healthy: an older Mac sidecar went on failing good encodes overnight with a measurement bug the
/// server and the sidecar source had both already fixed. The container and both sidecars ship in a
/// single release, so an older sidecar is pointed at the release page for the server's version,
/// which carries the matching Mac and Windows downloads.</para>
///
/// <para>Only the leading release number is compared: the Mac appends its build number, Windows
/// its commit, and a development server reports the release it was last synchronised to. A
/// sidecar that reports nothing readable is Unknown, never warned; one ahead of the server is left
/// alone. This informs; installing stays a person's decision.</para>
/// </summary>
public static partial class SidecarUpdateCheck
{
    public const string ReleasesUrl = "https://github.com/Jellman86/optimisarr/releases/tag/v";

    public static SidecarUpdateStatus Assess(string? serverVersion, string? sidecarVersion)
    {
        if (Release(serverVersion) is not { } server || server == new Version(0, 0, 0)
            || Release(sidecarVersion) is not { } sidecar)
        {
            return SidecarUpdateStatus.Unknown;
        }

        var latest = Format(server);
        return sidecar.CompareTo(server) switch
        {
            < 0 => new SidecarUpdateStatus(SidecarUpdateState.UpdateAvailable, latest, ReleasesUrl + latest),
            0 => new SidecarUpdateStatus(SidecarUpdateState.Current, latest, null),
            _ => new SidecarUpdateStatus(SidecarUpdateState.Newer, latest, null)
        };
    }

    private static Version? Release(string? text)
    {
        var match = text is null ? null : LeadingRelease().Match(text);
        return match is { Success: true }
            ? new Version(
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture))
            : null;
    }

    private static string Format(Version version) =>
        string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{version.Build}");

    [GeneratedRegex(@"^\s*v?(\d{1,6})\.(\d{1,6})\.(\d{1,6})")]
    private static partial Regex LeadingRelease();
}
