using System.Globalization;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class AudioQualityAssessmentTests
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(-1)]
    public void Unknown_or_too_short_audio_has_no_measurement_plan(double duration) =>
        Assert.Empty(AudioQualityWindowPlanner.Plan(duration));

    [Fact]
    public void Short_audio_is_measured_once_without_padding() =>
        Assert.Equal(new AudioQualityWindow(0, 3), Assert.Single(AudioQualityWindowPlanner.Plan(3)));

    [Fact]
    public void Samples_are_bounded_disjoint_and_include_the_end()
    {
        var windows = AudioQualityWindowPlanner.Plan(300);
        Assert.Equal(3, windows.Count);
        Assert.Equal(0, windows[0].StartSeconds);
        Assert.Equal(300, windows[^1].StartSeconds + windows[^1].DurationSeconds, 5);
        Assert.All(windows, w => Assert.InRange(w.DurationSeconds, 1, 30));
        Assert.Equal(90, windows.Sum(w => w.DurationSeconds), 5);
        for (var i = 1; i < windows.Count; i++)
            Assert.True(windows[i].StartSeconds >= windows[i - 1].StartSeconds + windows[i - 1].DurationSeconds);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(45)]
    [InlineData(61)]
    [InlineData(89.9)]
    [InlineData(89.9999791667)]
    public void Medium_audio_is_covered_by_disjoint_windows(double duration)
    {
        var windows = AudioQualityWindowPlanner.Plan(duration);
        Assert.All(windows, w => Assert.InRange(w.DurationSeconds, 1, 30));
        Assert.Equal(duration, windows.Sum(w => w.DurationSeconds), 3);
        for (var i = 1; i < windows.Count; i++)
            Assert.Equal(windows[i - 1].StartSeconds + windows[i - 1].DurationSeconds, windows[i].StartSeconds, 3);
    }

    [Fact]
    public void Paths_remain_literal_arguments_and_no_channel_mix_or_gain_change_is_applied()
    {
        var source = "-source [,] 日本語; $(touch nope).wav";
        var output = "output pcm";
        var args = AudioQualityCommandBuilder.Decode(Path.GetFullPath(source), output, new(1.25, 3));
        Assert.Contains(Path.GetFullPath(source), args);
        Assert.Equal(output, args[^1]);
        Assert.DoesNotContain("-ac", args);
        Assert.DoesNotContain("-af", args);
        Assert.Contains("48000", args);
        Assert.Contains("f32le", args);
        Assert.Contains("-n", args);
    }

    private static string Probe(string channels = "2", string duration = "19.17", string extra = "") =>
        $$$"""{"streams":[{"codec_type":"audio","channels":{{{channels}}},"sample_rate":"44100","channel_layout":"stereo","duration":"{{{duration}}}"}{{{extra}}}],"format":{"duration":"{{{duration}}}"}}""";

    [Fact]
    public void Multi_track_video_surround_and_invalid_probes_are_explicitly_unsupported()
    {
        Assert.NotNull(AudioQualityInput.Parse(Probe()));
        Assert.Null(AudioQualityInput.Parse(Probe(extra: ",{\"codec_type\":\"audio\"}")));
        Assert.Null(AudioQualityInput.Parse(Probe(extra: ",{\"codec_type\":\"video\"}")));
        Assert.Null(AudioQualityInput.Parse(Probe("6")));
        Assert.Null(AudioQualityInput.Parse(Probe(duration: "NaN")));
        Assert.Null(AudioQualityInput.Parse("[]"));
        Assert.Null(AudioQualityInput.Parse("broken"));
    }

    [Fact]
    public void Channel_loss_or_unexpected_duration_cannot_be_hidden_by_a_good_distance()
    {
        var stereo = AudioQualityInput.Parse(Probe())!;
        Assert.Null(AudioQualityInput.Incompatibility(stereo, stereo));
        Assert.NotNull(AudioQualityInput.Incompatibility(stereo, stereo with { Channels = 1, ChannelLayout = "mono" }));
        Assert.NotNull(AudioQualityInput.Incompatibility(stereo, stereo with { DurationSeconds = 10 }));
    }

    private static string Result(string distances = "[0.01,0.2]", string revision = AudioQualityResultParser.Revision, int frames = 144000) =>
        $$"""{"schema":1,"metric":"zimtohrli","revision":"{{revision}}","sampleRate":48000,"channels":2,"frames":{{frames}},"fullScaleSineDb":78.3,"distances":{{distances}}}""";

    [Fact]
    public void Every_channel_is_retained_and_the_worst_is_exposed_without_a_pass_verdict()
    {
        var result = AudioQualityResultParser.Parse(Result(), 2, 3)!;
        Assert.Equal(new[] { 0.01, 0.2 }, result.ChannelDistances);
        Assert.Equal(0.2, result.WorstChannelDistance);
    }

    [Theory]
    [InlineData("[0.01]")]
    [InlineData("[0.01,0.2,0.3]")]
    [InlineData("[-0.1,0]")]
    [InlineData("[0,1.01]")]
    [InlineData("[0,\"NaN\"]")]
    [InlineData("[0,null]")]
    public void Partial_or_invalid_channel_evidence_is_rejected(string distances) =>
        Assert.Null(AudioQualityResultParser.Parse(Result(distances), 2, 3));

    [Fact]
    public void Wrong_model_short_pcm_or_unexpected_json_is_rejected()
    {
        Assert.Null(AudioQualityResultParser.Parse(Result(revision: "different"), 2, 3));
        Assert.Null(AudioQualityResultParser.Parse(Result(frames: 1000), 2, 3));
        Assert.Null(AudioQualityResultParser.Parse("{}", 2, 3));
        Assert.Null(AudioQualityResultParser.Parse("[]", 2, 3));
        Assert.Null(AudioQualityResultParser.Parse(Result().Replace("48000", "44100"), 2, 3));
        Assert.Null(AudioQualityResultParser.Parse(Result().Replace("78.3", "80"), 2, 3));
        Assert.Null(AudioQualityResultParser.Parse(Result().Replace("\"schema\":1", "\"schema\":2"), 2, 3));
        Assert.Null(AudioQualityResultParser.Parse(Result().Replace("\"schema\":1", "\"schema\":2,\"schema\":1"), 2, 3));
    }

    [Fact]
    public void Preparation_and_result_parsing_are_independent_of_machine_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new("de-DE");
            Assert.Contains("1.25", AudioQualityCommandBuilder.Decode("in", "out", new(1.25, 3)));
            Assert.NotNull(AudioQualityInput.Parse(Probe()));
            Assert.NotNull(AudioQualityResultParser.Parse(Result(), 2, 3));
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }
}
