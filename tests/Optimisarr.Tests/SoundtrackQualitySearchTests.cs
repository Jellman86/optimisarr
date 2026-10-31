using System.Text.Json;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

public sealed class SoundtrackQualitySearchTests
{
    private static readonly VerificationPolicy Gated = VerificationPolicy.Default with
    {
        SoundtrackQualityGateEnabled = true,
        MaximumSoundtrackQualityDistance = 0.001
    };

    [Fact]
    public void Retained_tracks_are_frozen_in_output_order_and_removed_tracks_get_no_search()
    {
        var probe = Probe(Track(2, "eng"), Track(2, "fre"), Track(1, "eng", "Commentary", comment: true));

        var plan = SoundtrackSearchPlanner.Plan(probe, [1], "aac", 160, downmixToStereo: false, Gated);

        Assert.Null(plan.Unavailable);
        Assert.Equal([0, 2], plan.Tracks.Select(track => track.SourceAudioIndex));
        Assert.Equal([0, 1], plan.Tracks.Select(track => track.OutputIndex));
        Assert.Equal(["eng", "eng"], plan.Tracks.Select(track => track.Language));
        Assert.Equal("Commentary", plan.Tracks[1].Title);
        Assert.Equal(1, plan.Tracks[1].Input.Channels);
        Assert.All(plan.Tracks, track => Assert.Equal(3, track.Windows.Count));
        Assert.Equal(160, plan.Bitrates!.Baseline);
    }

    [Theory]
    [InlineData(6, "Surround")]
    [InlineData(2, null)]
    public void Any_retained_track_outside_the_assessable_domain_disables_the_whole_search(int channels, string? expected)
    {
        var probe = Probe(Track(2, "eng"), Track(channels, "eng"));

        var plan = SoundtrackSearchPlanner.Plan(probe, [], "libopus", 128, downmixToStereo: false, Gated);

        if (expected is null) Assert.Null(plan.Unavailable);
        else
        {
            Assert.Contains(expected, plan.Unavailable);
            Assert.Empty(plan.Tracks);
        }
    }

    [Fact]
    public void Search_requires_the_soundtrack_gate_that_will_judge_the_final_file()
    {
        var probe = Probe(Track(2, "eng"));

        Assert.NotNull(SoundtrackSearchPlanner.Plan(probe, [], "aac", 160, false, VerificationPolicy.Default).Unavailable);
        Assert.NotNull(SoundtrackSearchPlanner.Plan(probe, [], "aac", 160, false,
            Gated with { MaximumSoundtrackQualityDistance = null }).Unavailable);
    }

    [Fact]
    public void Unsupported_encoders_downmixes_and_too_many_tracks_are_refused()
    {
        var one = Probe(Track(2, "eng"));
        Assert.NotNull(SoundtrackSearchPlanner.Plan(one, [], "flac", 160, false, Gated).Unavailable);
        Assert.NotNull(SoundtrackSearchPlanner.Plan(one, [], null, 160, false, Gated).Unavailable);

        var nine = Probe(Enumerable.Range(0, 9).Select(_ => Track(2, "eng")).ToArray());
        Assert.Contains("eight", SoundtrackSearchPlanner.Plan(nine, [], "aac", 160, false, Gated).Unavailable);
        Assert.Null(SoundtrackSearchPlanner.Plan(nine, [8], "aac", 160, false, Gated).Unavailable);
    }

    [Fact]
    public void A_stereo_downmix_is_searched_only_when_every_retained_track_is_already_stereo()
    {
        // The downmix is one global -ac 2: a no-op for stereo, but it turns mono into stereo and
        // folds surround, neither of which a stereo sample of the source can stand for.
        Assert.Null(SoundtrackSearchPlanner.Plan(Probe(Track(2, "eng"), Track(2, "fre")), [], "aac", 160, true, Gated).Unavailable);
        Assert.Contains("already stereo", SoundtrackSearchPlanner.Plan(Probe(Track(2, "eng"), Track(1, "eng")), [], "aac", 160, true, Gated).Unavailable);
        Assert.NotNull(SoundtrackSearchPlanner.Plan(Probe(Track(6, "eng")), [], "aac", 160, true, Gated).Unavailable);
        Assert.Null(SoundtrackSearchPlanner.Plan(Probe(Track(2, "eng"), Track(6, "eng")), [1], "aac", 160, true, Gated).Unavailable);
    }

    [Fact]
    public void An_invalid_removal_list_is_refused_rather_than_guessed()
    {
        var probe = Probe(Track(2, "eng"));

        Assert.NotNull(SoundtrackSearchPlanner.Plan(probe, [3], "aac", 160, false, Gated).Unavailable);
        Assert.NotNull(SoundtrackSearchPlanner.Plan(probe, [0], "aac", 160, false, Gated).Unavailable);
        Assert.NotNull(SoundtrackSearchPlanner.Plan("{ not json", [], "aac", 160, false, Gated).Unavailable);
    }

    [Fact]
    public void Reference_and_candidate_samples_address_one_source_track_without_overwriting()
    {
        var window = new PerceptualAudioWindow(600, 30);
        var reference = PerceptualSamplePlanner.SoundtrackReference("film with spaces.mkv", "reference.wav", 2, window).ToList();
        Assert.Contains("0:a:2", reference);
        Assert.Contains("pcm_f32le", reference);
        Assert.Contains("-n", reference);
        Assert.DoesNotContain("-y", reference);
        Assert.Equal("reference.wav", reference[^1]);

        var spec = new TranscodeSpec("film with spaces.mkv", "film.mkv", "libx265", 24, null, false,
            AudioEncoder: "libopus", AudioBitrateKbps: 128, AudioFilter: "volume=1.0");
        var candidate = PerceptualSamplePlanner.SoundtrackCandidate(spec, "reference.wav", "candidate.opus", 96, window);
        Assert.Equal("libopus", candidate[candidate.IndexOf("-c:a") + 1]);
        Assert.Equal("96k", candidate[candidate.IndexOf("-b:a") + 1]);
        Assert.Equal("volume=1.0", candidate[candidate.IndexOf("-af") + 1]);
        Assert.Contains("-vn", candidate);
        Assert.Contains("-n", candidate);
        Assert.DoesNotContain("-y", candidate);
        Assert.Equal("candidate.opus", candidate[^1]);
    }

    [Fact]
    public void A_lossy_source_window_is_prepared_at_its_exact_length_and_the_candidate_encodes_that_same_audio()
    {
        // A lossy source decodes in whole packets, so a plain -t cut misses the window by hundreds of
        // samples and the assessment refuses the pair. Pad and trim the reference to the window, then
        // encode the candidate from it so the only difference measured is the codec under test.
        var window = new PerceptualAudioWindow(339, 30);
        var reference = PerceptualSamplePlanner.SoundtrackReference("film.mkv", "reference.wav", 0, window).ToList();
        Assert.Equal("329", reference[reference.IndexOf("-ss") + 1]);
        Assert.Single(reference, argument => argument == "-ss");
        Assert.DoesNotContain("-t", reference);
        Assert.Equal("aresample=48000,atrim=start_sample=480000,asetpts=PTS-STARTPTS,apad,atrim=end_sample=1440000",
            reference[reference.IndexOf("-af") + 1]);

        var spec = new TranscodeSpec("film.mkv", "out.mkv", "libx265", 24, null, false, AudioEncoder: "aac", AudioBitrateKbps: 128);
        var candidate = PerceptualSamplePlanner.SoundtrackCandidate(spec, "reference.wav", "candidate.m4a", 96, window);
        Assert.Equal("reference.wav", candidate[candidate.IndexOf("-i") + 1]);
        Assert.Single(candidate, argument => argument == "-i");
        Assert.DoesNotContain("-ss", candidate);
        Assert.DoesNotContain("film.mkv", candidate);
    }

    [Theory]
    [InlineData("aac", ".m4a")]
    [InlineData("libopus", ".opus")]
    [InlineData("libmp3lame", ".mp3")]
    public void Samples_use_the_standalone_container_the_assessment_accepts(string encoder, string extension) =>
        Assert.Equal(extension, PerceptualSamplePlanner.SoundtrackSampleExtension(encoder));

    [Fact]
    public void Selected_rates_apply_to_their_exact_output_tracks()
    {
        var spec = new TranscodeSpec("film.mkv", "out.mkv", "libx265", 24, null, false,
            AudioEncoder: "aac", AudioBitrateKbps: 160) { AudioTrackBitratesKbps = [96, 128] };

        var arguments = FfmpegCommandBuilder.Build(spec, 0, "libx265", optimisedMarker: null).ToList();

        Assert.Equal("96k", arguments[arguments.IndexOf("-b:a:0") + 1]);
        Assert.Equal("128k", arguments[arguments.IndexOf("-b:a:1") + 1]);
        Assert.DoesNotContain("-b:a", arguments);
    }

    [Fact]
    public void Per_track_rates_are_only_offered_to_workers_that_accept_them()
    {
        var spec = new TranscodeSpec(WorkerProtocol.InputPlaceholder, WorkerProtocol.OutputPlaceholder + ".mkv", "libx265", 24, null, false,
            AudioEncoder: "aac", AudioBitrateKbps: 160);
        var library = FfmpegCommandBuilder.Build(spec, 0, "libx265", optimisedMarker: null);
        var measured = FfmpegCommandBuilder.Build(spec with { AudioTrackBitratesKbps = [96, 128] }, 0, "libx265", optimisedMarker: null);

        Assert.True(WorkerProtocol.MinimumForEncodeCommand(library) < WorkerProtocol.TrackBitrateVersion);
        Assert.Equal(WorkerProtocol.TrackBitrateVersion, WorkerProtocol.MinimumForEncodeCommand(measured));
        Assert.True(WorkerProtocol.Current >= WorkerProtocol.TrackBitrateVersion);
    }

    [Fact]
    public void Without_selected_rates_every_track_keeps_the_library_bitrate()
    {
        var spec = new TranscodeSpec("film.mkv", "out.mkv", "libx265", 24, null, false, AudioEncoder: "aac", AudioBitrateKbps: 160);

        var arguments = FfmpegCommandBuilder.Build(spec, 0, "libx265", optimisedMarker: null).ToList();

        Assert.Equal("160k", arguments[arguments.IndexOf("-b:a") + 1]);
        Assert.DoesNotContain("-b:a:0", arguments);
    }

    [Fact]
    public void Summary_selects_per_track_rates_only_when_every_track_was_searched()
    {
        var probe = Probe(Track(2, "eng"), Track(2, "fre"));
        var plan = SoundtrackSearchPlanner.Plan(probe, [], "aac", 160, false, Gated);
        var chosen = new PerceptualSearchResult([], new(null, 96, false, "done"));
        var fellBack = new PerceptualSearchResult([], new(null, 160, true, "no evidence"));

        var complete = new SoundtrackSearchSummary("aac", 160, 0.001, new string('a', 64),
            [new(plan.Tracks[0], chosen), new(plan.Tracks[1], fellBack)], null);
        var refused = new SoundtrackSearchSummary("aac", 160, 0.001, new string('a', 64), [], "Surround");

        Assert.Equal([96, 160], complete.SelectedBitrates());
        Assert.Null(refused.SelectedBitrates());
        var json = JsonSerializer.Serialize(complete);
        Assert.Equal([96, 160], JsonSerializer.Deserialize<SoundtrackSearchSummary>(json)!.SelectedBitrates());
    }

    private static string Probe(params object[] audio) => JsonSerializer.Serialize(new
    {
        format = new { duration = "3600.000", start_time = "0.000000" },
        streams = new object[] { new { codec_type = "video", start_time = "0.000000" } }.Concat(audio).ToArray()
    });

    private static object Track(int channels, string language, string? title = null, bool comment = false) => new
    {
        codec_type = "audio",
        channels,
        sample_rate = "48000",
        channel_layout = channels switch { 1 => "mono", 2 => "stereo", _ => "5.1(side)" },
        duration = "3600.000",
        start_time = "0.000000",
        tags = title is null ? (object)new { language } : new { language, title },
        disposition = new { comment = comment ? 1 : 0 }
    };
}
