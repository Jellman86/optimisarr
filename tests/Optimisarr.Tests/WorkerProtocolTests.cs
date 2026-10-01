using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

/// <summary>
/// Version negotiation is the gate that stops an upgraded control plane from silently scheduling
/// a job onto a sidecar that cannot speak the same contract. It must fail closed: when the two
/// ranges do not overlap, no version is agreed and the reason says which side is out of range.
/// </summary>
public class WorkerProtocolTests
{
    [Fact]
    public void V1_measurements_require_candidate_format_protocol_but_legacy_jobs_remain_compatible()
    {
        Assert.Equal(7, WorkerProtocol.MinimumForAssignment(["-c:v:0", "hevc_nvenc"], Optimisarr.Core.Domain.MediaKind.Video, "vmaf_v1.0.16_3d0h", true));
        Assert.Equal(1, WorkerProtocol.MinimumForAssignment(["-c:v:0", "hevc_nvenc"], Optimisarr.Core.Domain.MediaKind.Video, "vmaf_v0.6.1", true));
        Assert.Equal(1, WorkerProtocol.MinimumForAssignment(["-c:v:0", "hevc_nvenc"], Optimisarr.Core.Domain.MediaKind.Video, "vmaf_v1.0.16_3d0h", false));
    }

    [Fact]
    public void Explicit_range_commands_require_protocol_five_without_upgrading_ordinary_commands()
    {
        Assert.Equal(5, WorkerProtocol.MinimumForEncodeCommand(["-bsf:v:0", "h264_metadata=video_full_range_flag=0"]));
        Assert.Equal(5, WorkerProtocol.MinimumForEncodeCommand(["-color_range:v:0", "pc", "-c:s:0", "ass"]));
        Assert.Equal(4, WorkerProtocol.MinimumForEncodeCommand(["-c:s:0", "ass"]));
        Assert.Equal(1, WorkerProtocol.MinimumForEncodeCommand(["-c:v:0", "hevc_nvenc"]));
        Assert.Equal(4, WorkerProtocol.Negotiate(1, 4).AgreedVersion);
    }

    [Fact]
    public void Indexed_subtitle_overrides_require_the_new_command_contract()
    {
        Assert.Equal(4, WorkerProtocol.MinimumForEncodeCommand(["-c:s:0", "ass"]));
        Assert.Equal(1, WorkerProtocol.MinimumForEncodeCommand(["-c:s", "copy"]));
    }

    [Fact]
    public void Negotiate_agrees_the_current_version_when_the_worker_supports_it()
    {
        var result = WorkerProtocol.Negotiate(
            workerMinimum: WorkerProtocol.MinimumSupported,
            workerMaximum: WorkerProtocol.Current);

        Assert.True(result.Compatible);
        Assert.Equal(WorkerProtocol.Current, result.AgreedVersion);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void Negotiate_agrees_the_highest_version_both_sides_support()
    {
        // A worker built against a newer contract must fall back to what this control plane speaks,
        // never the other way round — the main app owns the contract.
        var result = WorkerProtocol.Negotiate(
            workerMinimum: WorkerProtocol.MinimumSupported,
            workerMaximum: WorkerProtocol.Current + 5);

        Assert.True(result.Compatible);
        Assert.Equal(WorkerProtocol.Current, result.AgreedVersion);
    }

    [Fact]
    public void Negotiate_refuses_a_worker_that_is_too_old()
    {
        var result = WorkerProtocol.Negotiate(
            workerMinimum: WorkerProtocol.MinimumSupported - 2,
            workerMaximum: WorkerProtocol.MinimumSupported - 1);

        Assert.False(result.Compatible);
        Assert.Equal(0, result.AgreedVersion);
        Assert.Contains("older", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Negotiate_refuses_a_worker_that_is_entirely_newer()
    {
        // The worker cannot speak anything this build understands. Refusing is the safe outcome;
        // guessing that a newer sidecar is backwards compatible is exactly the silent-upgrade
        // failure this negotiation exists to prevent.
        var result = WorkerProtocol.Negotiate(
            workerMinimum: WorkerProtocol.Current + 1,
            workerMaximum: WorkerProtocol.Current + 3);

        Assert.False(result.Compatible);
        Assert.Equal(0, result.AgreedVersion);
        Assert.Contains("newer", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Negotiate_refuses_an_inverted_range()
    {
        var result = WorkerProtocol.Negotiate(workerMinimum: 9, workerMaximum: 2);

        Assert.False(result.Compatible);
        Assert.Equal(0, result.AgreedVersion);
        Assert.Contains("range", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Current_is_at_least_the_minimum_supported()
    {
        Assert.True(WorkerProtocol.Current >= WorkerProtocol.MinimumSupported);
    }
}
