using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

/// <summary>
/// Whether a paired sidecar is older than the server it works for. The Mac reports "0.2.15 (11)",
/// Windows "0.2.14+d211f33…"; the server's own assembly version is "0.2.15.0".
/// </summary>
public sealed class SidecarUpdateCheckTests
{
    [Theory]
    [InlineData("0.2.14 (9)", "0.2.15.0")]
    [InlineData("0.2.14+d211f3360833d1532d86b5563e3eb4bed823efa3", "0.2.15.0")]
    [InlineData("0.1.5", "0.2.15.0")]
    [InlineData("0.2.9", "0.2.15.0")]
    public void An_older_sidecar_is_told_where_the_matching_release_is(string sidecar, string server)
    {
        var status = SidecarUpdateCheck.Assess(server, sidecar);

        Assert.Equal(SidecarUpdateState.UpdateAvailable, status.State);
        Assert.Equal("0.2.15", status.LatestVersion);
        // The container and both sidecars ship in one release, so its page holds the downloads.
        Assert.Equal("https://github.com/Jellman86/optimisarr/releases/tag/v0.2.15", status.ReleaseUrl);
    }

    [Theory]
    [InlineData("0.2.15 (11)")]
    [InlineData("0.2.15+abc")]
    [InlineData("0.2.15")]
    public void A_sidecar_on_the_servers_release_is_current(string sidecar)
    {
        var status = SidecarUpdateCheck.Assess("0.2.15.0", sidecar);

        Assert.Equal(SidecarUpdateState.Current, status.State);
        Assert.Null(status.ReleaseUrl);
    }

    [Fact]
    public void A_sidecar_newer_than_the_server_is_never_told_to_update()
    {
        var status = SidecarUpdateCheck.Assess("0.2.15.0", "0.2.16 (12)");

        Assert.Equal(SidecarUpdateState.Newer, status.State);
        Assert.Null(status.ReleaseUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("dev")]
    [InlineData("unknown")]
    public void A_sidecar_that_does_not_say_is_unknown_rather_than_warned(string? sidecar)
    {
        var status = SidecarUpdateCheck.Assess("0.2.15.0", sidecar);

        Assert.Equal(SidecarUpdateState.Unknown, status.State);
        Assert.Null(status.ReleaseUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.0.0.0")]
    public void A_server_without_a_real_version_warns_nobody(string? server)
    {
        // A local build with no version set must not tell every worker to "update" to 0.0.0.
        Assert.Equal(SidecarUpdateState.Unknown, SidecarUpdateCheck.Assess(server, "0.2.14 (9)").State);
    }
}
