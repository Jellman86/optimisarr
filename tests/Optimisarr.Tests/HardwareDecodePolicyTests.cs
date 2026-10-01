using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class HardwareDecodePolicyTests
{
    [Theory]
    [InlineData("High", true)]
    [InlineData("Main", true)]
    [InlineData("Constrained Baseline", true)]
    [InlineData("High 4:4:4 Predictive", false)]
    [InlineData("High 10", false)]
    [InlineData(null, false)]
    public void A_worker_only_hardware_decodes_profiles_covered_by_its_round_trip(string? profile, bool expected) =>
        Assert.Equal(expected, HardwareDecodePolicy.SupportsProvedWorkerSource("h264", "yuv420p", profile));

    [Fact]
    public void Clipped_disposable_video_uses_software_decode_for_frame_exact_comparison()
    {
        Assert.False(HardwareDecodePolicy.ShouldUse(
            configured: true,
            isDisposable: true,
            MediaKind.Video,
            videoCodec: "hevc",
            clipSeconds: 60));
    }

    [Fact]
    public void Normal_video_keeps_configured_hardware_decode()
    {
        Assert.True(HardwareDecodePolicy.ShouldUse(
            configured: true,
            isDisposable: false,
            MediaKind.Video,
            videoCodec: "hevc",
            clipSeconds: null));
    }

    [Theory]
    [InlineData(MediaKind.Audio)]
    [InlineData(MediaKind.Image)]
    public void Non_video_disposable_work_does_not_change_the_global_decode_choice(MediaKind kind)
    {
        Assert.True(HardwareDecodePolicy.ShouldUse(
            configured: true,
            isDisposable: true,
            kind,
            videoCodec: null,
            clipSeconds: 15));
    }

    [Fact]
    public void Disabled_hardware_decode_remains_disabled()
    {
        Assert.False(HardwareDecodePolicy.ShouldUse(
            configured: false,
            isDisposable: false,
            MediaKind.Video,
            videoCodec: "hevc",
            clipSeconds: null));
    }

    [Fact]
    public void A_downscale_forces_software_decode_so_the_scale_filter_has_frames_it_can_read()
    {
        // A software scale filter fed GPU surfaces from a hardware decoder fails the whole graph.
        // The encoder side is unaffected; only where the frames are decoded changes.
        Assert.False(HardwareDecodePolicy.ShouldUse(
            configured: true, isDisposable: false, MediaKind.Video, "hevc", clipSeconds: null,
            requiresSoftwareFilter: true));
    }

    [Fact]
    public void Without_a_software_filter_the_decode_choice_is_unchanged()
    {
        Assert.True(HardwareDecodePolicy.ShouldUse(
            configured: true, isDisposable: false, MediaKind.Video, "hevc", clipSeconds: null,
            requiresSoftwareFilter: false));
    }
}
