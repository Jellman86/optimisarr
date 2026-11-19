using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class SilenceTrimTests
{
    // Captured from the shipped FFmpeg 6.1 with -progress pipe:2 on 3 s silence, 4 s tone, 1.5 s
    // silence, 2 s tone, 3.2 s silence (13.7 s). The progress time is where the last frame starts.
    private const string Detected = """
        [silencedetect @ 0x5596edf13cc0] silence_start: 0
        [silencedetect @ 0x5596edf13cc0] silence_end: 3.00005 | silence_duration: 3.00005
        out_time_us=6000000
        [silencedetect @ 0x5596edf13cc0] silence_start: 7
        [silencedetect @ 0x5596edf13cc0] silence_end: 8.50002 | silence_duration: 1.50002
        [silencedetect @ 0x5596edf13cc0] silence_start: 10.5
        [silencedetect @ 0x5596edf13cc0] silence_end: 13.7 | silence_duration: 3.2
        out_time_us=13688163
        progress=end
        """;

    private static SilenceMeasurement Measured() => SilenceDetectParser.Parse(Detected)!;

    private static readonly SilenceTrimSource Music = new(Probed: true, ContainerSeconds: 13.7, AudioStreamCount: 1,
        SampleRate: 44_100, StartIsTimed: false);

    [Fact]
    public void Silencedetect_output_is_read_into_intervals_and_the_decoded_length()
    {
        var measured = Measured();

        Assert.Equal(
            [new SilenceInterval(0, 3.00005), new SilenceInterval(7, 8.50002), new SilenceInterval(10.5, 13.7)],
            measured.Intervals);
        // The last silence ends after the last frame starts, so it is the better measure of the end.
        Assert.Equal(13.7, measured.DecodedSeconds);
    }

    [Fact]
    public void A_silence_left_open_runs_to_the_decoded_end_and_a_negative_start_counts_from_zero()
    {
        var measured = SilenceDetectParser.Parse("""
            [silencedetect @ 0x1] silence_start: -0.01
            [silencedetect @ 0x1] silence_end: 2 | silence_duration: 2.01
            [silencedetect @ 0x1] silence_start: 9
            out_time_us=12000000
            """)!;

        Assert.Equal([new SilenceInterval(0, 2), new SilenceInterval(9, 12)], measured.Intervals);
    }

    [Fact]
    public void A_tag_echoed_to_stderr_cannot_invent_a_silence()
    {
        var measured = SilenceDetectParser.Parse("""
              LYRICS          : see silencedetect silence_start: 0
                              : and silencedetect silence_end: 100
            out_time_us=120000000
            """)!;

        Assert.Empty(measured.Intervals);
    }

    [Fact]
    public void A_pass_that_never_reported_how_far_it_decoded_is_not_a_measurement()
    {
        Assert.Null(SilenceDetectParser.Parse("[silencedetect @ 0x1] silence_start: 0\n[silencedetect @ 0x1] silence_end: 3"));
        Assert.Empty(SilenceDetectParser.Parse("Input #0, flac\nsilence_start: 1\nout_time_us=5000000")!.Intervals);
    }

    [Fact]
    public void Silence_at_both_edges_is_trimmed_keeping_a_little_of_each_and_the_pause_inside_stays()
    {
        var plan = Assert.IsType<AudioTrimPlan>(SilenceTrimPlanner.Decide(Music, Measured(), -60).Plan);

        Assert.Equal((long)Math.Floor((3.00005 - 0.25) * 44_100), plan.StartSample);
        Assert.Equal((long)Math.Ceiling((10.5 + 0.25) * 44_100), plan.EndSample);
        Assert.Equal(8.0, plan.KeptSeconds, 2);
        Assert.Equal(-60, plan.ThresholdDb);
    }

    [Fact]
    public void A_file_whose_container_is_shorter_than_its_audio_is_never_trimmed()
    {
        // A chained Ogg file: ffprobe says 21.78 s, the audio decodes to 57.97 s. Trimming against
        // the container would cut the music after 21 s and still match its own plan.
        var chained = SilenceDetectParser.Parse("""
            [silencedetect @ 0x1] silence_start: 0
            [silencedetect @ 0x1] silence_end: 2.99932 | silence_duration: 2.99932
            out_time_us=57965714
            """);

        var decision = SilenceTrimPlanner.Decide(Music with { ContainerSeconds = 21.78 }, chained, -60);

        Assert.Null(decision.Plan);
        Assert.Contains("decodes to 57.97 s but says it is 21.78 s long", decision.Reason);
    }

    [Fact]
    public void A_file_whose_end_is_not_trimmed_keeps_everything_to_its_end()
    {
        var measured = new SilenceMeasurement([new SilenceInterval(0, 3)], 60);

        var plan = SilenceTrimPlanner.Plan(measured, 44_100, -60).Plan!;

        Assert.Null(plan.EndSample);
        Assert.False(plan.TrimsEnd);
        Assert.DoesNotContain("end_sample", plan.FilterExpression);
        Assert.Equal(57.25, plan.KeptSeconds, 2);
    }

    [Fact]
    public void Trailing_silence_is_judged_against_the_decoded_end_not_a_padded_container()
    {
        // LAME: 10.01 s of music then 3 s of silence. The container says 13.06 s, but the audio ends
        // at 13.01 s; judged against the container the trailing silence would be missed.
        var mp3 = SilenceDetectParser.Parse("""
            [silencedetect @ 0x1] silence_start: 10.0103
            out_time_us=12983923
            [silencedetect @ 0x1] silence_end: 13.01 | silence_duration: 2.99971
            """)!;

        Assert.Equal(13.01 - 10.0103, SilenceTrimPlanner.Edges(mp3).Trailing, 3);
    }

    [Fact]
    public void A_hidden_track_after_a_long_gap_is_kept_because_that_gap_does_not_reach_the_end()
    {
        var decision = SilenceTrimPlanner.Plan(new SilenceMeasurement([new SilenceInterval(240, 540)], 600), 44_100, -60);

        Assert.Null(decision.Plan);
        Assert.Equal("No silence at the start or end.", decision.Reason);
    }

    [Fact]
    public void An_all_silent_file_or_one_left_too_short_is_not_trimmed()
    {
        Assert.Null(SilenceTrimPlanner.Plan(new SilenceMeasurement([new SilenceInterval(0, 5)], 5), 44_100, -60).Plan);
        Assert.Null(SilenceTrimPlanner.Plan(new SilenceMeasurement([new SilenceInterval(0, 2), new SilenceInterval(2.4, 3.5)], 3.5), 44_100, -60).Plan);
    }

    [Fact]
    public void Silence_shorter_than_the_kept_padding_is_left_alone()
    {
        Assert.Null(SilenceTrimPlanner.Plan(new SilenceMeasurement([new SilenceInterval(0, 0.2)], 60), 48_000, -60).Plan);
    }

    [Fact]
    public void A_file_timed_from_its_start_only_loses_its_trailing_silence()
    {
        var plan = SilenceTrimPlanner.Decide(Music with { StartIsTimed = true }, Measured(), -60).Plan!;

        Assert.Equal(0, plan.StartSample);
        Assert.False(plan.TrimsStart);
        Assert.True(plan.TrimsEnd);
    }

    [Theory]
    [InlineData(false, 13.7, 1, "could not be probed")]
    [InlineData(true, null, 1, "could not be probed")]
    [InlineData(true, 13.7, 2, "more than one audio stream")]
    public void A_file_that_cannot_be_judged_is_not_trimmed(bool probed, double? seconds, int streams, string reason)
    {
        var decision = SilenceTrimPlanner.Decide(Music with { Probed = probed, ContainerSeconds = seconds, AudioStreamCount = streams }, Measured(), -60);

        Assert.Null(decision.Plan);
        Assert.Contains(reason, decision.Reason);
    }

    [Fact]
    public void A_failed_measurement_is_not_trimmed()
    {
        Assert.Contains("could not be measured", SilenceTrimPlanner.Decide(Music, null, -60).Reason);
    }

    [Theory]
    [InlineData("cuesheet", "FILE \"album.flac\" WAVE", true)]
    [InlineData("LYRICS", "[00:12.30]First line", true)]
    [InlineData("SYNCEDLYRICS", "[00:12.30]First line", true)]
    [InlineData("unsyncedlyrics", "[01:02]Words", true)]
    [InlineData("lyrics-eng", "Plain words, no times", false)]
    [InlineData("title", "[01:00] in a title", false)]
    public void Tags_timed_from_the_start_are_recognised(string key, string value, bool timed)
    {
        Assert.Equal(timed, SilenceTrimPlanner.HasStartTimedTags(new Dictionary<string, string> { [key] = value }));
    }

    [Fact]
    public void A_plan_round_trips_through_the_text_stored_on_the_job()
    {
        var both = new AudioTrimPlan(121_252, 474_075, 44_100, 13.7, -60);
        var startOnly = new AudioTrimPlan(121_252, null, 44_100, 13.7, -55.5);

        Assert.Equal(both, AudioTrimPlan.TryParse(both.ToString()));
        Assert.Equal(startOnly, AudioTrimPlan.TryParse(startOnly.ToString()));
        Assert.Null(AudioTrimPlan.TryParse("10:5:44100:13.7:-60"));
        Assert.Null(AudioTrimPlan.TryParse("0:end:44100:13.7:-60"));
        Assert.Null(AudioTrimPlan.TryParse("121252:474075:44100:13.7"));
        Assert.Null(AudioTrimPlan.TryParse("none"));
        Assert.Null(AudioTrimPlan.TryParse(null));
    }

    [Fact]
    public void Detection_reads_only_the_first_audio_stream_reports_progress_and_writes_nothing()
    {
        var args = SilenceDetectCommandBuilder.Build("/music/a b.flac", -60, 2);

        Assert.Equal(["-hide_banner", "-nostats", "-nostdin", "-progress", "pipe:2", "-i", "/music/a b.flac", "-map", "0:a:0",
            "-af", "silencedetect=noise=-60dB:d=2", "-f", "null", "-"], args);
    }

    private static TranscodeSpec Music_() => new(
        InputPath: "/data/music/Track.flac", OutputPath: "/work/Track.opus", VideoCodec: null, Crf: null,
        Preset: null, TonemapToSdr: false, Kind: MediaKind.Audio, AudioEncoder: "libopus", AudioBitrateKbps: 128,
        SourceAudioChannelLayout: "5.1(side)")
    { SilenceTrim = new AudioTrimPlan(100, 5000, 48_000, 1.0, -60) };

    [Fact]
    public void The_trim_comes_first_in_the_one_audio_filter_chain()
    {
        var args = FfmpegCommandBuilder.Build(Music_());

        var chain = args[args.ToList().IndexOf("-af") + 1];
        Assert.StartsWith("atrim=start_sample=100:end_sample=5000,asetpts=PTS-STARTPTS,", chain);
        Assert.Equal(chain, FfmpegCommandBuilder.AudioFilterChain(Music_()));
        Assert.Single(args, "-af");
    }

    [Fact]
    public void A_sample_a_clip_or_a_video_is_never_trimmed()
    {
        Assert.DoesNotContain("atrim", FfmpegCommandBuilder.AudioFilterChain(Music_() with { AudioSampleOnly = true }) ?? "");
        Assert.DoesNotContain("atrim", FfmpegCommandBuilder.AudioFilterChain(Music_() with { ClipSeconds = 30 }) ?? "");
        Assert.DoesNotContain("atrim", FfmpegCommandBuilder.AudioFilterChain(Music_() with { Kind = MediaKind.Video }) ?? "");
    }
}
