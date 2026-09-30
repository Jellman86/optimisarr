using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class SubtitleTimelineCompatibilityTests
{
    [Theory]
    [InlineData("23,1.000,1.500\n23,1.000,2.000")]
    [InlineData("23,1.000,1.500\n23,1.500,2.000")]
    [InlineData("23,1.000,1.500\n23,0.500,1.000")]
    public void Simultaneous_overlapping_or_reordered_kept_cues_require_matroska(string packets)
        => Assert.True(SubtitleTimelineCompatibility.RequiresMatroska(packets, [23]));

    [Fact]
    public void Adjacent_cues_and_independent_tracks_can_remain_in_mp4()
        => Assert.False(SubtitleTimelineCompatibility.RequiresMatroska(
            "2,1.000,1.500\n23,1.000,2.000\n2,2.500,1.000\n23,3.000,1.000", [2, 23]));

    [Fact]
    public void Removed_tracks_do_not_change_the_output_container()
        => Assert.False(SubtitleTimelineCompatibility.RequiresMatroska(
            "2,1.000,1.500\n23,1.000,2.000\n23,1.000,3.000", [2]));

    [Theory]
    [InlineData("23,N/A,1.500")]
    [InlineData("23,1.000,N/A")]
    [InlineData("23,1.000,0")]
    [InlineData("23,NaN,1.000")]
    [InlineData("23,1.000,Infinity")]
    public void Unproven_kept_cue_timelines_use_the_preserving_container(string packets)
        => Assert.True(SubtitleTimelineCompatibility.RequiresMatroska(packets, [23]));

    [Fact]
    public void A_kept_track_without_packet_evidence_requires_matroska()
        => Assert.True(SubtitleTimelineCompatibility.RequiresMatroska("2,1,1", [2, 23]));

    [Fact]
    public void Sub_microsecond_rounding_at_a_cue_boundary_is_tolerated()
        => Assert.False(SubtitleTimelineCompatibility.RequiresMatroska("23,1,1.0000001\n23,2,1", [23]));

    [Fact]
    public void No_kept_subtitles_needs_no_container_fallback()
        => Assert.False(SubtitleTimelineCompatibility.RequiresMatroska("23,1,1\n23,1,2", []));
}
