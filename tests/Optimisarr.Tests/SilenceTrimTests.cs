using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class SilenceTrimTests
{
    // Captured from the shipped FFmpeg 6.1 on 3 s silence, 4 s tone, 1.5 s silence, 2 s tone, 3.2 s silence.
    private const string Detected = """
        [silencedetect @ 0x5596edf13cc0] silence_start: 0
        [silencedetect @ 0x5596edf13cc0] silence_end: 3.00005 | silence_duration: 3.00005
        [silencedetect @ 0x5596edf13cc0] silence_start: 7
        [silencedetect @ 0x5596edf13cc0] silence_end: 8.50002 | silence_duration: 1.50002
        [silencedetect @ 0x5596edf13cc0] silence_start: 10.5
        [silencedetect @ 0x5596edf13cc0] silence_end: 13.7 | silence_duration: 3.2
        """;

    [Fact]
    public void Silencedetect_output_is_read_into_intervals()
    {
        var intervals = SilenceDetectParser.ParseAll(Detected, 13.7);

        Assert.Equal(
            [new SilenceInterval(0, 3.00005), new SilenceInterval(7, 8.50002), new SilenceInterval(10.5, 13.7)],
            intervals);
    }

    [Fact]
    public void A_silence_left_open_runs_to_the_end_and_a_negative_start_counts_from_zero()
    {
        var intervals = SilenceDetectParser.ParseAll("""
            [silencedetect @ 0x1] silence_start: -0.01
            [silencedetect @ 0x1] silence_end: 2 | silence_duration: 2.01
            [silencedetect @ 0x1] silence_start: 9
            """, 12);

        Assert.Equal([new SilenceInterval(0, 2), new SilenceInterval(9, 12)], intervals);
    }

    [Fact]
    public void Unrelated_or_garbled_lines_are_ignored()
    {
        Assert.Empty(SilenceDetectParser.ParseAll("Input #0, flac\nsilence_start: 1\n[silencedetect @ 0x1] silence_end: x", 10));
    }

    [Fact]
    public void Silence_at_both_edges_is_trimmed_keeping_a_little_of_each_and_the_pause_inside_stays()
    {
        var decision = SilenceTrimPlanner.Plan(SilenceDetectParser.ParseAll(Detected, 13.7), 13.7, 44_100);

        var plan = Assert.IsType<AudioTrimPlan>(decision.Plan);
        Assert.Equal((long)Math.Floor((3.00005 - 0.25) * 44_100), plan.StartSample);
        Assert.Equal((long)Math.Ceiling((10.5 + 0.25) * 44_100), plan.EndSample);
        Assert.Equal(8.0, plan.KeptSeconds, 2);
        Assert.True(plan.TrimsStart);
        Assert.True(plan.TrimsEnd);
    }

    [Fact]
    public void A_hidden_track_after_a_long_gap_is_kept_because_that_gap_does_not_reach_the_end()
    {
        // Song, five minutes of silence, hidden track that plays to the end.
        var decision = SilenceTrimPlanner.Plan([new SilenceInterval(240, 540)], 600, 44_100);

        Assert.Null(decision.Plan);
        Assert.Equal("No silence at the start or end.", decision.Reason);
    }

    [Fact]
    public void An_all_silent_file_or_one_left_too_short_is_not_trimmed()
    {
        Assert.Null(SilenceTrimPlanner.Plan([new SilenceInterval(0, 5)], 5, 44_100).Plan);
        Assert.Null(SilenceTrimPlanner.Plan([new SilenceInterval(0, 2), new SilenceInterval(2.4, 3.5)], 3.5, 44_100).Plan);
    }

    [Fact]
    public void Silence_shorter_than_the_kept_padding_is_left_alone()
    {
        Assert.Null(SilenceTrimPlanner.Plan([new SilenceInterval(0, 0.2)], 60, 48_000).Plan);
    }

    [Fact]
    public void A_file_timed_from_its_start_only_loses_its_trailing_silence()
    {
        var plan = SilenceTrimPlanner.Plan(SilenceDetectParser.ParseAll(Detected, 13.7), 13.7, 44_100, trimStart: false).Plan!;

        Assert.Equal(0, plan.StartSample);
        Assert.False(plan.TrimsStart);
        Assert.True(plan.TrimsEnd);
    }

    [Fact]
    public void An_unknown_length_or_sample_rate_plans_nothing()
    {
        Assert.Null(SilenceTrimPlanner.Plan([new SilenceInterval(0, 3)], 0, 44_100).Plan);
        Assert.Null(SilenceTrimPlanner.Plan([new SilenceInterval(0, 3)], 60, 0).Plan);
    }

    [Fact]
    public void A_plan_round_trips_through_the_text_stored_on_the_job()
    {
        var plan = new AudioTrimPlan(121_252, 474_075, 44_100, 13.7);

        Assert.Equal(plan, AudioTrimPlan.TryParse(plan.ToString()));
        Assert.Null(AudioTrimPlan.TryParse("10:5:44100:13.7"));
        Assert.Null(AudioTrimPlan.TryParse("none"));
        Assert.Null(AudioTrimPlan.TryParse(null));
    }

    [Fact]
    public void Detection_reads_only_the_first_audio_stream_with_every_channel_together_and_writes_nothing()
    {
        var args = SilenceDetectCommandBuilder.Build("/music/a b.flac", -60, 2);

        Assert.Equal(["-hide_banner", "-nostats", "-nostdin", "-i", "/music/a b.flac", "-map", "0:a:0",
            "-af", "silencedetect=noise=-60dB:d=2", "-f", "null", "-"], args);
    }

    private static TranscodeSpec Music() => new(
        InputPath: "/data/music/Track.flac", OutputPath: "/work/Track.opus", VideoCodec: null, Crf: null,
        Preset: null, TonemapToSdr: false, Kind: MediaKind.Audio, AudioEncoder: "libopus", AudioBitrateKbps: 128,
        SourceAudioChannelLayout: "5.1(side)")
    { SilenceTrim = new AudioTrimPlan(100, 5000, 48_000, 1.0) };

    [Fact]
    public void The_trim_comes_first_in_the_one_audio_filter_chain()
    {
        var args = FfmpegCommandBuilder.Build(Music());

        var chain = args[args.ToList().IndexOf("-af") + 1];
        Assert.StartsWith("atrim=start_sample=100:end_sample=5000,asetpts=PTS-STARTPTS,", chain);
        Assert.Equal(chain, FfmpegCommandBuilder.AudioFilterChain(Music()));
        Assert.Single(args, "-af");
    }

    [Fact]
    public void A_sample_a_clip_or_a_video_is_never_trimmed()
    {
        Assert.DoesNotContain("atrim", FfmpegCommandBuilder.AudioFilterChain(Music() with { AudioSampleOnly = true }) ?? "");
        Assert.DoesNotContain("atrim", FfmpegCommandBuilder.AudioFilterChain(Music() with { ClipSeconds = 30 }) ?? "");
        Assert.DoesNotContain("atrim", FfmpegCommandBuilder.AudioFilterChain(Music() with { Kind = MediaKind.Video }) ?? "");
    }
}
