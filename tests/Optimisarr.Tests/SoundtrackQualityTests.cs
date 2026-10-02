using System.Text.Json;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class SoundtrackQualityTests
{
    internal static string Probe(params object[] audio) => JsonSerializer.Serialize(new
    {
        streams = new object[] { new { codec_type = "video", codec_name = "h264", start_time = "0" } }.Concat(audio),
        format = new { duration = "3", start_time = "0" }
    });

    internal static object Track(string language = "eng", string title = "Main", int channels = 2,
        string start = "0", string duration = "3") => new
    {
        codec_type = "audio", codec_name = "aac", channels, sample_rate = "48000",
        channel_layout = channels == 1 ? "mono" : channels == 2 ? "stereo" : "5.1",
        start_time = start, duration, tags = new { language, title }, disposition = new { comment = 0 }
    };

    [Fact]
    public void Every_retained_track_maps_to_its_output_position_after_language_removal()
    {
        var plan = SoundtrackQualityPlanner.Plan(Probe(Track("fra"), Track(), Track("eng", "Commentary")),
            Probe(Track(), Track("eng", "Commentary")), new([0]));
        Assert.Null(plan.Error);
        Assert.Equal(new[] { 1, 2 }, plan.Tracks.Select(track => track.SourceAudioIndex));
        Assert.Equal(new[] { 0, 1 }, plan.Tracks.Select(track => track.CandidateAudioIndex));
        Assert.Equal("Commentary", plan.Tracks[1].Title);
    }

    [Fact]
    public void Reordered_languages_or_commentary_cannot_be_assessed_as_the_same_track()
    {
        var source = Probe(Track(), Track("fra", "Commentary"));
        Assert.NotNull(SoundtrackQualityPlanner.Plan(source, Probe(Track("fra", "Commentary"), Track()), new([])).Error);
        Assert.NotNull(SoundtrackQualityPlanner.Plan(Probe(Track()), Probe(Track(title: "Commentary")), new([])).Error);
    }

    [Theory]
    [InlineData(6, "0", "3")]
    [InlineData(2, "0.5", "3")]
    [InlineData(2, "0", "2")]
    public void Surround_shifted_or_truncated_tracks_have_no_valid_plan(int channels, string start, string duration) =>
        Assert.NotNull(SoundtrackQualityPlanner.Plan(Probe(Track()), Probe(Track(channels: channels, start: start, duration: duration)), new([])).Error);

    [Fact]
    public void Missing_tracks_invalid_removals_and_excessive_track_counts_are_refused()
    {
        Assert.NotNull(SoundtrackQualityPlanner.Plan(Probe(Track(), Track("fra")), Probe(Track()), new([])).Error);
        Assert.NotNull(SoundtrackQualityPlanner.Plan(Probe(Track()), Probe(Track()), new([2])).Error);
        Assert.NotNull(SoundtrackQualityPlanner.Plan(Probe(Track()), Probe(Track()), new([0, 0])).Error);
        var many = Enumerable.Range(0, 9).Select(_ => Track()).ToArray();
        Assert.NotNull(SoundtrackQualityPlanner.Plan(Probe(many), Probe(many), new([])).Error);
    }

    [Fact]
    public void Selected_video_audio_is_explicit_and_never_weakens_the_standalone_parser()
    {
        var json = Probe(Track("fra"), Track());
        Assert.Null(AudioQualityInput.Parse(json));
        Assert.Equal(2, AudioQualityInput.ParseTrack(json, 1)!.Channels);
        var args = AudioQualityCommandBuilder.Decode("source", "pcm", new(0, 3), 1);
        Assert.Equal("0:a:1", args[args.ToList().IndexOf("-map") + 1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioQualityCommandBuilder.Decode("source", "pcm", new(0, 3), -1));
    }

    [Fact]
    public async Task Strict_worker_observation_never_calls_the_server_measurement_and_missing_evidence_blocks_the_gate()
    {
        var service = new SoundtrackQualityObservationService((_, _, _, _, _) => throw new InvalidOperationException("Server fallback"));
        var remote = new Optimisarr.Core.Workers.RemoteVerificationEvidence(Guid.NewGuid(), new('a', 64), new('b', 64));
        var report = await service.ObserveAsync(true, true, true, false, "source", "candidate",
            Probe(Track()), Probe(Track()), new([]), remote, default);
        Assert.NotNull(report!.UnavailableReason);
        var verification = SoundtrackQualityGate.Apply(new([new("Decode", CheckOutcome.Passed, "Healthy")], SoundtrackQuality: report),
            VerificationPolicy.Default with { SoundtrackQualityGateEnabled = true, MaximumSoundtrackQualityDistance = 0.01 }, true);
        Assert.False(verification.Passed);
        Assert.False(verification.SoundtrackQuality!.GatePassed);
    }

    [Fact]
    public async Task Copied_or_preview_audio_does_not_launch_measurement_or_add_a_perceptual_gate()
    {
        var service = new SoundtrackQualityObservationService((_, _, _, _, _) => throw new InvalidOperationException("Unexpected measurement"));
        Assert.Null(await service.ObserveAsync(true, false, true, false, "source", "candidate", "", "", new([]), null, default));
        Assert.Null(await service.ObserveAsync(true, true, true, true, "source", "candidate", "", "", new([]), null, default));
        var report = new VerificationReport([new("Decode", CheckOutcome.Passed, "Healthy")]);
        Assert.Same(report, SoundtrackQualityGate.Apply(report, VerificationPolicy.Default with { SoundtrackQualityGateEnabled = true }, false));
    }

    [Fact]
    public void Standalone_audio_settings_do_not_enable_soundtrack_checks()
    {
        var policy = VerificationPolicy.Default with { AudioQualityReportingEnabled = true, AudioQualityGateEnabled = true };
        Assert.False(policy.RequiresSoundtrackQuality(true));
        Assert.True((policy with { SoundtrackQualityGateEnabled = true }).RequiresSoundtrackQuality(true));
        Assert.False((policy with { SoundtrackQualityGateEnabled = true }).RequiresSoundtrackQuality(false));
    }

    [Fact]
    public void Reusing_one_tracks_measurements_for_another_track_cannot_pass_remote_validation()
    {
        var plan = SoundtrackQualityPlanner.Plan(Probe(Track(), Track(title: "Commentary")), Probe(Track(), Track(title: "Commentary")), new([]));
        var result = new AudioQualityAssessmentResult(true, null, plan.Tracks[0].Reference, plan.Tracks[0].Candidate,
            new('a', 64), new('b', 64), new('c', 64), new('d', 64), new('e', 64), [new(new(0, 2.9), new(139200, [0, 0]))], 1,
            ReferenceAudioIndex: 0, CandidateAudioIndex: 0);
        var report = new SoundtrackQualityReport(plan.Tracks.Select(pair => new SoundtrackQualityTrack(pair,
            new("Worker", RemoteAudioQualityEvidence.From(result), null))).ToArray(), null);
        var validated = SoundtrackQualityObservationService.Validate(report, plan, new('a', 64), new('b', 64));
        Assert.Null(validated.Tracks[0].Report.UnavailableReason);
        Assert.NotNull(validated.Tracks[1].Report.UnavailableReason);
    }

    [Fact]
    public void Codec_priming_is_prepared_against_the_picture_timeline_and_unbounded_offsets_are_refused()
    {
        var json = Probe(Track()).Replace("\"format\":{\"duration\":\"3\",\"start_time\":\"0\"}", "\"format\":{\"duration\":\"3.023\",\"start_time\":\"-0.023\"}");
        var input = AudioQualityInput.ParseTrack(json, 0)!;
        Assert.Equal(0.023, input.ContainerLeadSeconds, 6);
        var args = AudioQualityCommandBuilder.Decode("source", "pcm", new(1, 1), 0, input.ContainerLeadSeconds);
        Assert.Contains("1.023", args);
        Assert.Null(AudioQualityInput.ParseTrack(json.Replace("-0.023", "-1"), 0));
    }
    [Fact]
    public void Normalising_container_origins_preserves_the_audio_video_offset()
    {
        var source = Probe(Track(start: "1.4"))
            .Replace("\"codec_name\":\"h264\",\"start_time\":\"0\"", "\"codec_name\":\"h264\",\"start_time\":\"1.4\"")
            .Replace("\"format\":{\"duration\":\"3\",\"start_time\":\"0\"}", "\"format\":{\"duration\":\"3\",\"start_time\":\"1.4\"}");
        Assert.Null(SoundtrackQualityPlanner.Plan(source, Probe(Track()), new([])).Error);
        Assert.NotNull(SoundtrackQualityPlanner.Plan(source, Probe(Track(start: "0.5")), new([])).Error);
    }

    [Fact]
    public void Null_nested_worker_assessments_become_unavailable_and_block_only_an_enabled_gate()
    {
        var plan = SoundtrackQualityPlanner.Plan(Probe(Track()), Probe(Track()), new([]));
        var malformed = new RemoteAudioQualityEvidence("zimtohrli", AudioQualityResultParser.Revision,
            AudioQualityResultParser.SoundtrackPreparation, null!);
        var observation = SoundtrackQualityObservationService.Validate(new([new(plan.Tracks[0], new("Worker", malformed, null))], null),
            plan, new('a', 64), new('b', 64));
        Assert.NotNull(observation.Tracks[0].Report.UnavailableReason);
        var report = new VerificationReport([new("Decode", CheckOutcome.Passed, "Healthy")], SoundtrackQuality: observation);
        Assert.True(SoundtrackQualityGate.Apply(report, VerificationPolicy.Default, true).Passed);
        Assert.False(SoundtrackQualityGate.Apply(report, VerificationPolicy.Default with { SoundtrackQualityGateEnabled = true,
            MaximumSoundtrackQualityDistance = 0.01 }, true).Passed);
    }

    [Fact]
    public void Selected_zero_seek_does_not_drop_codec_priming_and_resampled_samples_are_bounded()
    {
        var args = AudioQualityCommandBuilder.Decode("source", "pcm", new(0, 3), 0, 0, soundtrack: true);
        Assert.DoesNotContain("-ss", args);
        Assert.Equal("3.1", args[args.ToList().IndexOf("-t") + 1]);
        Assert.Contains("aresample=48000,atrim=end_sample=144000", args);
        Assert.Contains("-ss", AudioQualityCommandBuilder.Decode("source", "pcm", new(0, 3)));
    }

    [Fact]
    public void Soundtrack_windows_leave_encoder_padding_outside_the_assessed_coverage()
    {
        var windows = AudioQualityWindowPlanner.Plan(8, soundtrack: true);
        Assert.Equal(7.9, windows.Sum(window => window.DurationSeconds), 6);
        Assert.Equal(119.9, AudioQualityWindowPlanner.Plan(120, soundtrack: true).Last().StartSeconds + 30, 6);
        Assert.Equal(8, AudioQualityWindowPlanner.Plan(8).Single().DurationSeconds);
    }

}
