using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class SourceTimelineAssessmentTests
{
    [Fact]
    public void A_two_packet_source_measurement_is_indeterminate_when_audio_and_output_span_the_episode()
    {
        Assert.True(SourceTimelineAssessment.IsIndeterminate(0.08, 1277.27, 1279.24));
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
