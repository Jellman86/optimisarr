using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class AudioChannelLayoutPolicyTests
{
    [Fact]
    public void Plan_maps_the_side_surround_layout_onto_the_standard_one_for_opus()
    {
        var plan = AudioChannelLayoutPolicy.Plan("libopus", "5.1(side)");

        Assert.Equal(AudioChannelLayoutOutcome.Normalised, plan.Outcome);
        Assert.Equal("channelmap=channel_layout=5.1", plan.Filter);
        Assert.Equal("5.1(side)", plan.SourceLayout);
        Assert.Equal("5.1", plan.TargetLayout);
        Assert.True(plan.Applies);
        Assert.False(plan.Blocked);
    }

    [Fact]
    public void Plan_leaves_a_standard_surround_source_alone()
    {
        var plan = AudioChannelLayoutPolicy.Plan("libopus", "5.1");

        Assert.Equal(AudioChannelLayoutOutcome.AlreadyStandard, plan.Outcome);
        Assert.Null(plan.Filter);
        Assert.False(plan.Applies);
    }

    [Theory]
    [InlineData("stereo")]
    [InlineData("mono")]
    [InlineData("7.1")]
    [InlineData("5.1(back)")]
    public void Plan_leaves_a_source_that_opus_encodes_as_it_is_alone(string layout)
    {
        var plan = AudioChannelLayoutPolicy.Plan("libopus", layout);

        Assert.Equal(AudioChannelLayoutOutcome.AlreadyStandard, plan.Outcome);
        Assert.Null(plan.Filter);
    }

    [Theory]
    [InlineData("aac")]
    [InlineData("libmp3lame")]
    [InlineData(null)]
    public void Plan_does_not_touch_a_codec_that_is_not_opus(string? encoder)
    {
        var plan = AudioChannelLayoutPolicy.Plan(encoder, "5.1(side)");

        Assert.Equal(AudioChannelLayoutOutcome.NotOpus, plan.Outcome);
        Assert.Null(plan.Filter);
    }

    [Fact]
    public void Plan_reports_an_unknown_source_layout_rather_than_guessing_one()
    {
        var plan = AudioChannelLayoutPolicy.Plan("libopus", null);

        Assert.Equal(AudioChannelLayoutOutcome.UnknownLayout, plan.Outcome);
        Assert.Null(plan.Filter);
    }

    [Fact]
    public void Plan_reports_a_disabled_conversion_rather_than_applying_it()
    {
        var plan = AudioChannelLayoutPolicy.Plan("libopus", "5.1(side)", enabled: false);

        Assert.Equal(AudioChannelLayoutOutcome.Disabled, plan.Outcome);
        Assert.Null(plan.Filter);
    }

    [Fact]
    public void Plan_never_overrides_an_audio_filter_that_is_already_set()
    {
        var plan = AudioChannelLayoutPolicy.Plan("libopus", "5.1(side)", existingAudioFilter: "loudnorm=I=-16");

        Assert.Equal(AudioChannelLayoutOutcome.ConflictingFilter, plan.Outcome);
        Assert.Null(plan.Filter);
        Assert.True(plan.Blocked);
    }

    [Fact]
    public void Plan_reports_a_stereo_downmix_as_blocking_the_surround_map()
    {
        var plan = AudioChannelLayoutPolicy.Plan("libopus", "5.1(side)", downmixToStereo: true);

        Assert.Equal(AudioChannelLayoutOutcome.DownmixedToStereo, plan.Outcome);
        Assert.Null(plan.Filter);
        Assert.True(plan.Blocked);
    }

    [Fact]
    public void Plan_matches_the_source_layout_regardless_of_its_casing_or_padding()
    {
        var plan = AudioChannelLayoutPolicy.Plan("libopus", " 5.1(SIDE) ");

        Assert.Equal(AudioChannelLayoutOutcome.Normalised, plan.Outcome);
        Assert.Equal("channelmap=channel_layout=5.1", plan.Filter);
    }

    [Fact]
    public void Every_outcome_explains_itself_for_the_operator()
    {
        var outcomes = new[]
        {
            AudioChannelLayoutPolicy.Plan("libopus", "5.1(side)"),
            AudioChannelLayoutPolicy.Plan("libopus", "5.1"),
            AudioChannelLayoutPolicy.Plan("aac", "5.1(side)"),
            AudioChannelLayoutPolicy.Plan("libopus", null),
            AudioChannelLayoutPolicy.Plan("libopus", "5.1(side)", enabled: false),
            AudioChannelLayoutPolicy.Plan("libopus", "5.1(side)", downmixToStereo: true),
            AudioChannelLayoutPolicy.Plan("libopus", "5.1(side)", existingAudioFilter: "loudnorm")
        };

        Assert.All(outcomes, plan => Assert.False(string.IsNullOrWhiteSpace(plan.Reason)));
    }
}
