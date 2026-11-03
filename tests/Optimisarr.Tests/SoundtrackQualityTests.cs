using System.Text.Json;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class SoundtrackQualityTests
{
    [Fact]
    public void Native_swift_report_matches_the_server_profiles_windows_and_evidence_contract()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "soundtrack-swift.json")));
        var root = json.RootElement;
        var plan = SoundtrackQualityPlanner.Plan(root.GetProperty("sourceProbe").GetString()!, root.GetProperty("candidateProbe").GetString()!, new([]));
        Assert.Null(plan.Error);
        // The archived native capture used v1 preparation. Its profiles/windows still match;
        // raw v1 evidence must be refused by this release before checking the current envelope.
        var captured = root.GetProperty("soundtrackQuality").GetRawText();
        var legacy = JsonSerializer.Deserialize<SoundtrackQualityReport>(captured, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.All(SoundtrackQualityObservationService.Validate(legacy, plan, root.GetProperty("sourceSha256").GetString()!, root.GetProperty("candidateSha256").GetString()!).Tracks,
            track => Assert.Null(track.Report.Evidence));
        var native = JsonSerializer.Deserialize<SoundtrackQualityReport>(captured.Replace("audio-f32le-48k-video-timeline-v1", AudioQualityResultParser.SoundtrackPreparation),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var validated = SoundtrackQualityObservationService.Validate(native, plan, root.GetProperty("sourceSha256").GetString()!, root.GetProperty("candidateSha256").GetString()!);
        Assert.Null(validated.UnavailableReason);
        Assert.Equal(2, validated.Tracks.Count);
        Assert.All(validated.Tracks, t => { Assert.Null(t.Report.UnavailableReason); Assert.NotNull(t.Report.Evidence); });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-duration")]
    [InlineData("00:60:00")]
    public void Missing_or_invalid_track_duration_never_borrows_the_container_length(string? end)
    {
        var track = Track();
        var json = Probe(track).Replace(",\"duration\":\"3\",\"tags\"", ",\"tags\"");
        if (end is not null) json = json.Replace("\"language\":\"eng\"", $"\"DURATION\":\"{end}\",\"language\":\"eng\"");
        Assert.Null(AudioQualityInput.ParseTrack(json, 0));
        Assert.NotNull(AudioQualityInput.Parse(json.Replace("{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"start_time\":\"0\"},", "")));
    }

    [Theory]
    [InlineData("libebml", "00:01:40.000000000")]
    [InlineData("Lavf62", "00:01:40.500000000")]
    public void Mkvtoolnix_elapsed_tags_and_ffmpeg_end_tags_agree_for_delayed_tracks(string writer, string end)
    {
        var json = """{"streams":[{"codec_type":"video","start_time":"0"},{"codec_type":"audio","start_time":"0.5","channels":2,"sample_rate":"48000","tags":{"DURATION":"END","_STATISTICS_WRITING_APP":"mkvmerge v95"}}],"format":{"start_time":"0","duration":"101","tags":{"encoder":"WRITER"}}}""".Replace("END", end).Replace("WRITER", writer);
        if (writer.StartsWith("Lavf")) json = json.Replace(",\"_STATISTICS_WRITING_APP\":\"mkvmerge v95\"", "");
        Assert.Equal(100, AudioQualityInput.ParseTrack(json, 0)!.DurationSeconds);
    }

    [Theory]
    [InlineData("mkvmerge v95", "00:01:40.500000000")]
    [InlineData("mkvpropedit v95", "00:01:40.000000000")]
    public void Conflicting_matroska_duration_writers_are_unavailable_for_delayed_tracks(string statistics, string end)
    {
        var json = """{"streams":[{"codec_type":"video","start_time":"0"},{"codec_type":"audio","start_time":"0.5","channels":2,"sample_rate":"48000","tags":{"DURATION":"END","_STATISTICS_WRITING_APP":"WRITINGTOOL"}}],"format":{"start_time":"0","duration":"101","tags":{"encoder":"Lavf62"}}}""".Replace("END", end).Replace("WRITINGTOOL", statistics);
        Assert.Null(AudioQualityInput.ParseTrack(json, 0));
    }

    [Fact]
    public void Mkvmerge_language_suffixed_statistics_give_the_track_length()
    {
        // mkvmerge writes only DURATION-eng when the statistics tags carry a language.
        var json = """{"streams":[{"codec_type":"video","start_time":"0"},{"codec_type":"audio","start_time":"0","channels":2,"sample_rate":"48000","tags":{"language":"ita","DURATION-eng":"00:42:48.640000000","_STATISTICS_WRITING_APP-eng":"mkvmerge v51"}}],"format":{"start_time":"0","duration":"2568.661","tags":{"encoder":"libebml v1.4.1 + libmatroska v1.6.2"}}}""";

        Assert.Equal(2568.64, AudioQualityInput.ParseTrack(json, 0)!.DurationSeconds, 6);
        // FFmpeg muxed it and mkvpropedit later replaced the statistics (Red Dwarf, Lavf58): with no plain
        // DURATION left, the suffixed one is the length. A delayed track would still be ambiguous.
        var propedited = json.Replace("libebml v1.4.1 + libmatroska v1.6.2", "Lavf58.29.100").Replace("mkvmerge v51", "mkvpropedit v28.2.0");
        Assert.Equal(2568.64, AudioQualityInput.ParseTrack(propedited, 0)!.DurationSeconds, 6);
        Assert.Null(AudioQualityInput.ParseTrack(propedited.Replace("\"start_time\":\"0\",\"channels\"", "\"start_time\":\"0.5\",\"channels\""), 0));
        Assert.Null(AudioQualityInput.ParseTrack(propedited, 0, writtenByFfmpeg: true));
        Assert.Null(AudioQualityInput.ParseTrack(json.Replace(",\"_STATISTICS", ",\"DURATION-fre\":\"00:42:40.000000000\",\"_STATISTICS"), 0));
    }

    [Fact]
    public void An_output_this_ffmpeg_wrote_reads_its_own_end_tag_despite_copied_statistics()
    {
        // FFmpeg rewrites DURATION as an end timestamp but copies the source's stale mkvmerge statistics.
        var json = """{"streams":[{"codec_type":"video","start_time":"0"},{"codec_type":"audio","start_time":"0.019","channels":2,"sample_rate":"48000","tags":{"language":"eng","DURATION":"00:11:48.199000000","DURATION-eng":"00:11:48.139000000","_STATISTICS_WRITING_APP":"mkvmerge v9.6.0"}}],"format":{"start_time":"0","duration":"708.285","tags":{"encoder":"Lavf62.3.103"}}}""";

        Assert.Null(AudioQualityInput.ParseTrack(json, 0));
        Assert.Equal(708.18, AudioQualityInput.ParseTrack(json, 0, writtenByFfmpeg: true)!.DurationSeconds, 6);
        Assert.Null(AudioQualityInput.ParseTrack(json.Replace("\"DURATION\":\"00:11:48.199000000\",", ""), 0, writtenByFfmpeg: true));
    }

    [Theory]
    [InlineData("DURATION")]
    [InlineData("DURATION-eng")]
    public void Flac_statistics_use_the_exact_sample_count_and_still_refuse_truncation(string tag)
    {
        // 4,321,280 samples; the statistics end at the start of the final 4,096-sample packet.
        var source = FlacStatisticsProbe(tag);
        var reference = AudioQualityInput.ParseTrack(source, 0)!;
        Assert.Equal(4321280 / 48000.0, reference.DurationSeconds, 8);
        var honest = Probe(Track(title: null, duration: "90.048"));
        Assert.Null(SoundtrackQualityPlanner.Plan(source, honest, new([])).Error);
        Assert.Null(AudioQualityInput.Incompatibility(reference, AudioQualityInput.ParseTrack(honest, 0)!));
        var truncated = Probe(Track(title: null, duration: "89.915"));
        Assert.Contains("duration", SoundtrackQualityPlanner.Plan(source, truncated, new([])).Error);
        Assert.NotNull(AudioQualityInput.Incompatibility(reference, AudioQualityInput.ParseTrack(truncated, 0)!));
    }

    [Fact]
    public async Task Targeted_flac_statistics_probe_ignores_font_attachments_and_matches_worker_profiles()
    {
        var full = System.Text.Json.Nodes.JsonNode.Parse(FlacStatisticsProbe("DURATION-eng"))!;
        var streams = full["streams"]!.AsArray();
        var track = streams[1]!;
        track["index"] = 2;
        var targeted = new System.Text.Json.Nodes.JsonObject { ["streams"] = new System.Text.Json.Nodes.JsonArray(track.DeepClone()) }.ToJsonString();
        track.AsObject().Remove("extradata");
        streams.Insert(1, new System.Text.Json.Nodes.JsonObject { ["codec_type"] = "attachment", ["index"] = 1,
            ["codec_name"] = "ttf", ["extradata_size"] = 2 * 1024 * 1024 });
        var metadata = full.ToJsonString();
        var calls = 0;
        var enriched = await FlacStreamInfoProbe.EnrichAsync(metadata, async (index, token) =>
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(0, index); // audio-relative index, not the absolute stream index (2).
            calls++;
            await Task.Yield();
            return targeted;
        }, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.True(enriched.Length < 4096);
        var reference = AudioQualityInput.ParseTrack(enriched, 0)!;
        Assert.Equal(4321280 / 48000.0, reference.DurationSeconds, 8);
        Assert.Equal(AudioQualityInput.ParseTrack(FlacStatisticsProbe("DURATION-eng"), 0), reference);
        Assert.Null(SoundtrackQualityPlanner.Plan(enriched, Probe(Track(title: null, duration: "90.048")), new([])).Error);
        Assert.Contains("duration", SoundtrackQualityPlanner.Plan(enriched, Probe(Track(title: null, duration: "89.915")), new([])).Error);
        var args = FlacStreamInfoProbe.Arguments("movie.mkv", 0).ToList();
        Assert.Equal("a:0", args[args.IndexOf("-select_streams") + 1]);
        Assert.Contains("-show_data", args);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Flac_stream_duration_and_ffmpeg_output_need_no_extra_probe(bool streamDuration, bool writtenByFfmpeg)
    {
        var probe = FlacStatisticsProbe("DURATION");
        if (streamDuration) probe = probe.Replace("\"sample_rate\"", "\"duration\":\"90.027\",\"sample_rate\"");
        Assert.Equal(probe, await FlacStreamInfoProbe.EnrichAsync(probe, (_, _) => throw new Exception("Unexpected probe"),
            CancellationToken.None, writtenByFfmpeg));
    }

    [Fact]
    public async Task Oversized_or_wrong_track_streaminfo_is_refused()
    {
        var metadata = FlacStatisticsProbe("DURATION-eng").Replace("extradata", "unavailable");
        foreach (var targeted in new[] { new string('x', 16385), "null", "{}", "{\"streams\":null}", "{\"streams\":[]}",
            FlacStatisticsProbe("DURATION-eng"), FlacStatisticsProbe("DURATION-eng").Replace("flac", "ttf") })
        {
            var enriched = await FlacStreamInfoProbe.EnrichAsync(metadata, (_, _) => Task.FromResult(targeted), CancellationToken.None);
            Assert.Null(AudioQualityInput.ParseTrack(enriched, 0));
        }
    }

    [Fact]
    public void Flac_sample_counts_cannot_override_stream_durations_or_conflicting_statistics()
    {
        var json = FlacStatisticsProbe("DURATION-eng");
        Assert.Equal(91, AudioQualityInput.ParseTrack(json.Replace("\"sample_rate\"", "\"duration\":\"91\",\"sample_rate\""), 0)!.DurationSeconds);
        Assert.Null(AudioQualityInput.ParseTrack(json.Replace("29.941", "28.941"), 0));
        Assert.Null(AudioQualityInput.ParseTrack(json.Replace("02f0 0041", "02f0 0000").Replace("f000 0000", "0000 0000"), 0));
        Assert.Null(AudioQualityInput.ParseTrack(json.Replace("0bb8 02f0", "0ac4 42f0"), 0));
        Assert.Null(AudioQualityInput.ParseTrack(json.Replace("1000 1000", "1000 0000"), 0));
        Assert.Null(AudioQualityInput.ParseTrack(json.Replace("extradata", "unavailable"), 0));
        Assert.Null(AudioQualityInput.ParseTrack(json, 0, writtenByFfmpeg: true));
        Assert.Equal(90.1, AudioQualityInput.ParseTrack(FlacStatisticsProbe("DURATION").Replace("29.941", "30.100"), 0, writtenByFfmpeg: true)!.DurationSeconds);
        Assert.Equal(4321280 / 48000.0, AudioQualityInput.ParseTrack(json.Replace("29.941", "30.000"), 0)!.DurationSeconds, 8);
    }

    internal static string FlacStatisticsProbe(string tag) =>
        """{"streams":[{"codec_type":"video","start_time":"0"},{"codec_type":"audio","codec_name":"flac","start_time":"0","channels":2,"sample_rate":"48000","extradata":"\n00000000: 1000 1000 0000 0000 0000 0bb8 02f0 0041  ...............A\n00000010: f000 0000 0000 0000 0000 0000 0000 0000  ................\n00000020: 0000                                     ..\n","tags":{"language":"eng","TAG":"00:01:29.941000000","_STATISTICS_WRITING_APP-eng":"mkvpropedit v95"}}],"format":{"start_time":"0","duration":"91","tags":{"encoder":"Lavf62"}}}""".Replace("TAG", tag);

    [Theory]
    [InlineData(0, 0.007, "0.007")]
    [InlineData(0.04, 0.007, "0.047")]
    [InlineData(0, 0.021, "0.021")]
    [InlineData(0, 0, "0")]
    public void Near_start_soundtrack_windows_trim_on_the_picture_timeline_without_input_seeking(double start, double lead, string trim)
    {
        // The same command builder prepares reference and candidate, including a candidate with priming.
        foreach (var file in new[] { "reference-opus.mkv", "candidate-opus.mkv" })
        {
            var args = AudioQualityCommandBuilder.Decode(file, "decoded.raw", new(start, 30), containerLeadSeconds: lead, soundtrack: true).ToList();
            Assert.DoesNotContain("-ss", args);
            Assert.Equal($"aresample=48000,atrim=start={trim},asetpts=PTS-STARTPTS,atrim=end_sample=1440000", args[args.IndexOf("-af") + 1]);
            Assert.Equal("30.1", args[args.IndexOf("-t") + 1]);
            Assert.DoesNotContain("apad", string.Join(',', args));
        }
    }

    [Theory]
    [InlineData("0.009")]
    [InlineData("0.022")]
    public void Late_starting_soundtracks_count_samples_after_the_start_trim_without_a_timestamp_end(string start)
    {
        var probe = Probe(Track(start: start, duration: "8"));
        var profile = AudioQualityInput.ParseTrack(probe, 0)!;
        Assert.Equal(0, profile.ContainerLeadSeconds);
        var window = AudioQualityWindowPlanner.Plan(profile.DurationSeconds, soundtrack: true)[0];
        foreach (var file in new[] { "reference-late.mkv", "candidate-late.mkv" })
        {
            var args = AudioQualityCommandBuilder.Decode(file, "decoded.raw", window,
                containerLeadSeconds: profile.ContainerLeadSeconds, soundtrack: true).ToList();
            Assert.DoesNotContain("-ss", args);
            Assert.Equal("aresample=48000,atrim=start=0,asetpts=PTS-STARTPTS,atrim=end_sample=379200",
                args[args.IndexOf("-af") + 1]);
        }
    }

    [Fact]
    public void Mp4_outputs_that_cannot_carry_track_titles_keep_language_identity()
    {
        var source = Probe(Track("por", "Brazilian"), Track("eng", "English"));
        var mp4 = Probe(Track("por", null), Track("eng", null)).Replace("\"start_time\":\"0\"}}", "\"start_time\":\"0\",\"format_name\":\"mov,mp4,m4a,3gp,3g2,mj2\"}}");

        Assert.Null(SoundtrackQualityPlanner.Plan(source, mp4, new([])).Error);
        Assert.NotNull(SoundtrackQualityPlanner.Plan(source, Probe(Track("por", null), Track("eng", null)), new([])).Error);
        Assert.NotNull(SoundtrackQualityPlanner.Plan(source, mp4.Replace("\"por\"", "\"spa\""), new([])).Error);
        Assert.NotNull(SoundtrackQualityPlanner.Plan(source, Probe(Track("por", "English"), Track("eng", "Brazilian"))
            .Replace("\"start_time\":\"0\"}}", "\"start_time\":\"0\",\"format_name\":\"mov,mp4,m4a,3gp,3g2,mj2\"}}"), new([])).Error);
    }

    [Fact]
    public void A_downmixed_surround_track_is_assessed_against_the_same_stereo_downmix()
    {
        // The library downmixes to stereo, so the honest reference is the source through that same
        // downmix: the score then measures the codec, not the downmix the library chose.
        var source = Probe(Track("eng", "Main", channels: 6), Track("eng", "Commentary", channels: 1));
        var output = Probe(Track("eng", "Main"), Track("eng", "Commentary"));

        Assert.Contains("downmix", SoundtrackQualityPlanner.Plan(source, output, new([])).Error);
        var plan = SoundtrackQualityPlanner.Plan(source, output, new([], DownmixToStereo: true));

        Assert.Null(plan.Error);
        Assert.Equal(new AudioQualityInput(3, 2, 48000, "stereo", 0, SourceChannels: 6, SourceChannelLayout: "5.1"), plan.Tracks[0].Reference);
        Assert.Equal(new AudioQualityInput(3, 2, 48000, "stereo", 0, SourceChannels: 1, SourceChannelLayout: "mono"), plan.Tracks[1].Reference);
        Assert.Equal(new AudioQualityInput(3, 2, 48000, "stereo"), plan.Tracks[1].Candidate);
        Assert.Contains("audio filter", SoundtrackQualityPlanner.Plan(source, output, new([], DownmixToStereo: true, AudioFiltered: true)).Error);
        Assert.Null(SoundtrackQualityPlanner.Plan(Probe(Track()), Probe(Track()), new([], DownmixToStereo: true, AudioFiltered: true)).Error);
    }

    [Fact]
    public void A_surround_track_without_a_known_layout_is_not_downmixed_by_guesswork()
    {
        var source = Probe(Track("eng", "Main", channels: 6)).Replace("\"channel_layout\":\"5.1\",", "");
        Assert.NotNull(SoundtrackQualityPlanner.Plan(source, Probe(Track()), new([], DownmixToStereo: true)).Error);
        Assert.Null(AudioQualityInput.ParseTrack(source, 0, downmixToStereo: true));
    }

    [Fact]
    public void The_encode_and_both_references_downmix_with_one_argument_list()
    {
        var spec = new Optimisarr.Core.Queue.TranscodeSpec("film.mkv", "out.mkv", "libx265", 24, null, false,
            AudioEncoder: "aac", AudioBitrateKbps: 128, DownmixToStereo: true);
        var encode = Optimisarr.Core.Queue.FfmpegCommandBuilder.Build(spec, 0, "libx265", optimisedMarker: null).ToList();
        var channels = Optimisarr.Core.Queue.FfmpegCommandBuilder.StereoDownmixArguments;
        Assert.Equal(channels, encode.Skip(encode.IndexOf("-ac")).Take(channels.Count));

        var window = new AudioQualityWindow(10, 30);
        var reference = AudioQualityCommandBuilder.Decode("film.mkv", "r.raw", window, 1, 0, soundtrack: true, downmixToStereo: true).ToList();
        Assert.Equal(channels, reference.Skip(reference.IndexOf("-ac")).Take(channels.Count));
        Assert.DoesNotContain("-ac", AudioQualityCommandBuilder.Decode("out.mkv", "c.raw", window, 1, 0, soundtrack: true));

        var sample = Optimisarr.Core.Queue.PerceptualSamplePlanner.SoundtrackReference("film.mkv", "r.wav", 1,
            new Optimisarr.Core.Queue.PerceptualAudioWindow(10, 30), downmixToStereo: true).ToList();
        Assert.Equal(channels, sample.Skip(sample.IndexOf("-ac")).Take(channels.Count));
        Assert.DoesNotContain("-ac", Optimisarr.Core.Queue.PerceptualSamplePlanner.SoundtrackCandidate(spec, "r.wav", "c.m4a", 96,
            new Optimisarr.Core.Queue.PerceptualAudioWindow(10, 30)));
    }

    [Fact]
    public void Downmixed_evidence_is_bound_to_its_own_preparation()
    {
        var reference = new AudioQualityInput(100, 2, 48000, "stereo", 0, SourceChannels: 6, SourceChannelLayout: "5.1(side)");
        var plain = new AudioQualityAssessmentResult(true, null, reference with { SourceChannels = null, SourceChannelLayout = null },
            new AudioQualityInput(100, 2, 48000, "stereo"), null, null, null, null, null, [], 0, 0, 0);
        var downmixed = plain with { Reference = reference };

        Assert.Equal(AudioQualityResultParser.SoundtrackPreparation, plain.Preparation);
        Assert.Equal(AudioQualityResultParser.SoundtrackDownmixPreparation, downmixed.Preparation);
        Assert.Equal("The audio metric or preparation does not match this release.",
            AudioQualityReporting.Validate(RemoteAudioQualityEvidence.From(downmixed) with { Preparation = AudioQualityResultParser.SoundtrackPreparation },
                null, null, AudioQualityResultParser.SoundtrackDownmixPreparation));
    }

    [Fact]
    public void Matroska_track_end_tags_keep_short_soundtracks_inside_their_own_timeline()
    {
        var source = Probe(Track(duration: "12", start: "-0.021"))
            .Replace("\"duration\":\"12\",", "")
            .Replace("\"duration\":\"3\"", "\"duration\":\"12\"")
            .Replace("\"language\":\"eng\"", "\"DURATION\":\"00:00:11.520000000\",\"language\":\"eng\"")
            .Replace("\"start_time\":\"0\"}", "\"start_time\":\"-0.021\"}");
        var input = AudioQualityInput.ParseTrack(source, 0)!;
        Assert.Equal(11.541, input.DurationSeconds, 6);
        var windows = AudioQualityWindowPlanner.Plan(input.DurationSeconds, soundtrack: true);
        Assert.True(windows.Last().StartSeconds + windows.Last().DurationSeconds < 11.541);
        Assert.Null(AudioQualityInput.Incompatibility(input, input with { DurationSeconds = 11.541 }));
    }

    [Fact]
    public void Long_soundtrack_windows_use_fixed_frame_boundaries_and_explicit_nonzero_leads()
    {
        var windows = AudioQualityWindowPlanner.Plan(600, soundtrack: true);
        Assert.Equal(3, windows.Count);
        Assert.Equal(90, windows.Sum(w => w.DurationSeconds));
        Assert.Equal(599.9, windows.Last().StartSeconds + 30, 6);
        foreach (var w in windows)
        {
            var args = AudioQualityCommandBuilder.Decode("a", "b", w, 1, 0.0000111, soundtrack: true).ToArray();
            Assert.DoesNotContain("E", args[Array.IndexOf(args, args.Contains("-ss") ? "-ss" : "-af") + 1]);
            Assert.Equal("0:a:1", args[Array.IndexOf(args, "-map") + 1]);
        }
    }
    internal static string Probe(params object[] audio) => JsonSerializer.Serialize(new
    {
        streams = new object[] { new { codec_type = "video", codec_name = "h264", start_time = "0" } }.Concat(audio),
        format = new { duration = "3", start_time = "0" }
    });

    internal static object Track(string language = "eng", string? title = "Main", int channels = 2,
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
        Assert.Contains("aresample=48000,atrim=start=0,asetpts=PTS-STARTPTS,atrim=end_sample=144000", args);
        Assert.DoesNotContain("-ss", AudioQualityCommandBuilder.Decode("source", "pcm", new(0, 3)));
    }

    [Theory]
    [InlineData(0.2, 0.021, "0.221")]
    [InlineData(22.5, 0.021, "22.521")]
    public void Later_soundtrack_seeks_preserve_the_demuxer_origin_including_the_picture_lead(double start, double lead, string expected)
    {
        var args = AudioQualityCommandBuilder.Decode("source.mkv", "pcm", new(start, 3), 0, lead, soundtrack: true).ToList();
        var seek = args.IndexOf("-ss");
        Assert.True(seek >= 0 && seek < args.IndexOf("-i"));
        Assert.Equal(expected, args[seek + 1]);
        Assert.Single(args, arg => arg == "-ss");
        Assert.Contains("aresample=48000,atrim=end_sample=144000", args);
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
