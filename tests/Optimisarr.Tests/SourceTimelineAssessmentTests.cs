using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class SourceTimelineAssessmentTests
{
    [Theory]
    [InlineData(2900.814, 3070.25)]
    [InlineData(2898.395, 3268.863)]
    [InlineData(0.08, 1277.27)]
    public void A_materially_short_source_picture_span_needs_an_independent_confirmation(
        double videoSeconds, double audioSeconds)
    {
        Assert.True(SourceTimelineAssessment.NeedsConfirmation(videoSeconds, audioSeconds));
    }

    [Theory]
    [InlineData(1405.112, 1405.109)]
    [InlineData(3570.0, 3600.0)]
    [InlineData(null, 3600.0)]
    [InlineData(2900.0, null)]
    public void An_aligned_or_unmeasured_source_does_not_repeat_the_full_packet_scan(
        double? videoSeconds, double? audioSeconds)
    {
        Assert.False(SourceTimelineAssessment.NeedsConfirmation(videoSeconds, audioSeconds));
    }

    [Fact]
    public void A_two_packet_source_measurement_is_indeterminate_when_audio_and_output_span_the_episode()
    {
        Assert.True(SourceTimelineAssessment.IsIndeterminate(0.08, 1277.27, 1279.24));
    }

    [Fact]
    public void A_two_packet_source_measurement_is_indeterminate_without_output_when_source_metadata_spans_audio()
    {
        Assert.True(SourceTimelineAssessment.IsIndeterminate(0.08, 1277.27, null, 1279.24));
    }

    [Fact]
    public void A_genuinely_short_picture_is_not_excused_by_stream_duration_metadata()
    {
        Assert.False(SourceTimelineAssessment.IsIndeterminate(2362.943, 2881.365, null, 2881.365));
        Assert.False(SourceTimelineAssessment.IsIndeterminate(0.08, 1277.27, null, 0.08));
        Assert.False(SourceTimelineAssessment.IsIndeterminate(100, 3600, 101, 3600));
    }

    [Fact]
    public void A_short_source_with_an_equally_short_output_is_a_real_source_timeline_failure()
    {
        Assert.False(SourceTimelineAssessment.IsIndeterminate(2362.943, 2881.365, 2361.609));
    }

    [Fact]
    public void Metadata_duration_alone_does_not_make_a_packet_measurement_indeterminate()
    {
        Assert.False(SourceTimelineAssessment.IsIndeterminate(1405.112, 1405.109, null));
    }
}
