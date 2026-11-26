using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Rules;

namespace Optimisarr.Tests;

/// <summary>
/// The per-library minimum saving for track cleanup: how big each track is, how much removing the
/// unwanted ones saves, and when that is too little to be worth rewriting the file.
/// </summary>
public sealed class TrackRemovalSavingTests
{
    private const long Megabyte = 1024L * 1024L;
    private const long Gigabyte = 1024L * Megabyte;

    // ---- Probe: per-track sizes ----

    [Fact]
    public void Parse_reads_each_tracks_size_from_the_mkvmerge_statistics_tags()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "hevc", "tags": { "NUMBER_OF_BYTES": "40000000000" } },
            { "codec_type": "audio", "codec_name": "truehd", "tags": { "language": "eng", "NUMBER_OF_BYTES-eng": "4500000000" } },
            { "codec_type": "audio", "codec_name": "ac3", "tags": { "language": "fra", "NUMBER_OF_BYTES": "650000000" } },
            { "codec_type": "subtitle", "codec_name": "hdmv_pgs_subtitle", "tags": { "language": "deu", "NUMBER_OF_BYTES": "31000000" } },
            { "codec_type": "subtitle", "codec_name": "subrip", "tags": { "language": "eng", "NUMBER_OF_BYTES": "0" } }
          ],
          "format": { "format_name": "matroska,webm", "duration": "7200.0" }
        }
        """;

        var result = MediaProbeService.Parse(json, ".mkv");

        Assert.Equal(new long?[] { 4_500_000_000, 650_000_000 }, result.AudioTrackBytes);
        Assert.Equal(new long?[] { 31_000_000, 0 }, result.SubtitleTrackBytes);
    }

    [Fact]
    public void Parse_estimates_a_tracks_size_from_its_bitrate_and_its_own_duration()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "h264" },
            { "codec_type": "audio", "codec_name": "aac", "bit_rate": "128000", "duration": "100.0" }
          ],
          "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "7200.0" }
        }
        """;

        var result = MediaProbeService.Parse(json, ".mp4");

        Assert.Equal(new long?[] { 1_600_000 }, result.AudioTrackBytes);
    }

    [Fact]
    public void Parse_estimates_from_the_file_duration_when_the_track_reports_none()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "h264" },
            { "codec_type": "audio", "codec_name": "ac3", "bit_rate": "640000" }
          ],
          "format": { "format_name": "mpegts", "duration": "10.0" }
        }
        """;

        Assert.Equal(new long?[] { 800_000 }, MediaProbeService.Parse(json, ".ts").AudioTrackBytes);
    }

    [Theory]
    [InlineData("\"tags\": { \"NUMBER_OF_BYTES\": \"lots\" }")]
    [InlineData("\"tags\": { \"NUMBER_OF_BYTES\": \"-5\" }")]
    [InlineData("\"tags\": { \"NUMBER_OF_BYTES\": \"\" }")]
    [InlineData("\"tags\": { \"NUMBER_OF_BYTES\": 12345 }")]
    [InlineData("\"bit_rate\": \"N/A\"")]
    [InlineData("\"bit_rate\": \"0\"")]
    [InlineData("\"tags\": { \"language\": \"eng\" }")]
    public void Parse_leaves_a_tracks_size_unknown_when_neither_a_tag_nor_a_bitrate_can_be_trusted(string fields)
    {
        var json = $$"""
        {
          "streams": [
            { "codec_type": "video", "codec_name": "h264" },
            { "codec_type": "audio", "codec_name": "aac", {{fields}} }
          ],
          "format": { "format_name": "matroska,webm", "duration": "100.0" }
        }
        """;

        Assert.Equal(new long?[] { null }, MediaProbeService.Parse(json, ".mkv").AudioTrackBytes);
    }

    [Fact]
    public void Parse_leaves_a_bitrate_estimate_unknown_when_no_duration_is_reported()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "h264" },
            { "codec_type": "subtitle", "codec_name": "mov_text", "bit_rate": "200" }
          ],
          "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2" }
        }
        """;

        Assert.Equal(new long?[] { null }, MediaProbeService.Parse(json, ".mp4").SubtitleTrackBytes);
    }

    [Fact]
    public void Parse_falls_back_to_the_bitrate_when_the_statistics_tag_is_garbage()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "h264" },
            { "codec_type": "audio", "codec_name": "aac", "bit_rate": "8000", "duration": "1.0",
              "tags": { "NUMBER_OF_BYTES": "unknown" } }
          ],
          "format": { "format_name": "matroska,webm" }
        }
        """;

        Assert.Equal(new long?[] { 1_000 }, MediaProbeService.Parse(json, ".mkv").AudioTrackBytes);
    }

    // ---- Stored form ----

    [Fact]
    public void Track_sizes_round_trip_through_their_stored_form_keeping_unknown_entries_in_place()
    {
        var stored = TrackSizes.Format([4_500_000_000, null, 0]);

        Assert.Equal("4500000000, ?, 0", stored);
        Assert.Equal(new long?[] { 4_500_000_000, null, 0 }, TrackSizes.Parse(stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_stored_form_means_the_sizes_were_never_recorded(string? stored) =>
        Assert.Null(TrackSizes.Parse(stored));

    [Fact]
    public void A_garbled_stored_entry_reads_as_unknown_rather_than_as_a_size() =>
        Assert.Equal(new long?[] { 10, null, null }, TrackSizes.Parse("10, abc, -3"));

    [Fact]
    public void No_tracks_store_as_nothing() => Assert.Null(TrackSizes.Format([]));

    // ---- Measuring the saving ----

    [Fact]
    public void The_saving_adds_up_every_removed_audio_and_subtitle_track()
    {
        var saving = TrackRemovalSaving.Measure(
            100 * Gigabyte,
            [4 * Gigabyte, 1 * Gigabyte, 1 * Gigabyte], [1, 2],
            [10 * Megabyte, 20 * Megabyte], [1]);

        Assert.Equal(2 * Gigabyte + 20 * Megabyte, saving.Bytes);
        Assert.Equal(2.02, saving.Percent!.Value, precision: 2);
        Assert.Equal(TrackSavingKnowledge.Known, saving.Knowledge);
    }

    [Fact]
    public void A_removed_track_of_unknown_size_makes_the_whole_saving_unknown()
    {
        var saving = TrackRemovalSaving.Measure(10 * Gigabyte, [1 * Gigabyte, null], [1], null, []);

        Assert.Null(saving.Bytes);
        Assert.Equal(TrackSavingKnowledge.Unknown, saving.Knowledge);
    }

    [Fact]
    public void A_kept_track_of_unknown_size_does_not_matter()
    {
        var saving = TrackRemovalSaving.Measure(10 * Gigabyte, [null, 1 * Gigabyte], [1], null, []);

        Assert.Equal(1 * Gigabyte, saving.Bytes);
    }

    [Fact]
    public void Sizes_recorded_before_this_was_measured_say_so()
    {
        var saving = TrackRemovalSaving.Measure(10 * Gigabyte, null, [1], null, []);

        Assert.Equal(TrackSavingKnowledge.NotRecorded, saving.Knowledge);
    }

    [Fact]
    public void Sizes_that_no_longer_line_up_with_the_tracks_are_unknown()
    {
        var saving = TrackRemovalSaving.Measure(10 * Gigabyte, [1 * Gigabyte], [3], null, []);

        Assert.Equal(TrackSavingKnowledge.Unknown, saving.Knowledge);
    }

    // ---- The minimum ----

    [Fact]
    public void No_minimum_never_refuses()
    {
        var minimum = new TrackSavingMinimum(null, null);

        Assert.False(minimum.IsSet);
        Assert.Null(minimum.Refusal(TrackRemovalSaving.Measure(10 * Gigabyte, null, [0], null, [])));
    }

    [Fact]
    public void A_saving_below_the_percentage_is_refused_with_the_numbers()
    {
        var saving = TrackRemovalSaving.Measure(60 * Gigabyte, [120 * Megabyte], [0], null, []);

        var refusal = new TrackSavingMinimum(5, null).Refusal(saving);

        Assert.Equal("Removing these tracks would save about 120 MB (0.2%), below this library's minimum of 5%", refusal);
    }

    [Fact]
    public void A_saving_at_the_percentage_is_enough()
    {
        var saving = TrackRemovalSaving.Measure(100 * Gigabyte, [5 * Gigabyte], [0], null, []);

        Assert.Null(new TrackSavingMinimum(5, null).Refusal(saving));
    }

    [Fact]
    public void A_saving_below_the_size_is_refused()
    {
        var saving = TrackRemovalSaving.Measure(2 * Gigabyte, [300 * Megabyte], [0], null, []);

        Assert.Equal(
            "Removing these tracks would save about 300 MB (14.65%), below this library's minimum of 1 GB",
            new TrackSavingMinimum(null, 1 * Gigabyte).Refusal(saving));
    }

    [Fact]
    public void With_both_set_a_saving_must_meet_both()
    {
        var minimum = new TrackSavingMinimum(5, 1 * Gigabyte);

        // 10% of a 2 GB file meets the percentage but not the size.
        Assert.Equal(
            "Removing these tracks would save about 204.8 MB (10%), below this library's minimum of 5% and 1 GB",
            minimum.Refusal(TrackRemovalSaving.Measure(2 * Gigabyte, [2 * Gigabyte / 10], [0], null, [])));
        // 2 GB of a 100 GB file meets the size but not the percentage.
        Assert.NotNull(minimum.Refusal(TrackRemovalSaving.Measure(100 * Gigabyte, [2 * Gigabyte], [0], null, [])));
        Assert.Null(minimum.Refusal(TrackRemovalSaving.Measure(20 * Gigabyte, [2 * Gigabyte], [0], null, [])));
    }

    [Fact]
    public void An_unknown_saving_is_refused_rather_than_guessed()
    {
        var saving = TrackRemovalSaving.Measure(10 * Gigabyte, [null], [0], null, []);

        Assert.Equal(
            "The size of the tracks to remove is not recorded in this file and cannot be estimated, so this library's minimum saving cannot be checked",
            new TrackSavingMinimum(5, null).Refusal(saving));
    }

    [Fact]
    public void Sizes_never_recorded_ask_for_a_re_probe()
    {
        var saving = TrackRemovalSaving.Measure(10 * Gigabyte, null, [0], null, []);

        Assert.Equal(
            "Track sizes not recorded yet — re-probe the file to check this library's minimum saving",
            new TrackSavingMinimum(5, null).Refusal(saving));
    }

    // ---- Eligibility ----

    private static readonly RuleSettings Cleanup = RuleProfileDefaults.For(RuleProfile.TrackCleanup) with
    {
        KeepAudioLanguages = ["eng"],
    };

    private static MediaProperties Film(
        long sizeBytes,
        IReadOnlyList<long?>? audioBytes,
        string container = "matroska,webm",
        string relativePath = "Films/Example.mkv") =>
        new(container, "hevc", 3840, 2160, sizeBytes, false, relativePath,
            AudioLanguages: ["eng", "fra"], SubtitleLanguages: [])
        {
            AudioTrackBytes = audioBytes,
            SubtitleTrackBytes = null,
        };

    [Fact]
    public void Track_cleanup_without_a_minimum_behaves_as_before()
    {
        var decision = CandidateEvaluator.Evaluate(Film(60 * Gigabyte, null), Cleanup);

        Assert.True(decision.IsEligible);
        Assert.Equal("Remove 1 audio track(s) (fra) not in the kept languages", decision.Reason);
    }

    [Fact]
    public void Track_cleanup_skips_a_file_whose_removed_tracks_are_below_the_minimum()
    {
        var rules = Cleanup with { MinimumTrackRemovalSavingPercent = 5 };

        var decision = CandidateEvaluator.Evaluate(Film(60 * Gigabyte, [4 * Gigabyte, 120 * Megabyte]), rules);

        Assert.False(decision.IsEligible);
        Assert.Equal("Removing these tracks would save about 120 MB (0.2%), below this library's minimum of 5%", decision.Reason);
    }

    [Fact]
    public void Track_cleanup_offers_a_file_that_meets_the_minimum_and_says_how_much_it_saves()
    {
        var rules = Cleanup with { MinimumTrackRemovalSavingPercent = 5 };

        var decision = CandidateEvaluator.Evaluate(Film(60 * Gigabyte, [4 * Gigabyte, 6 * Gigabyte]), rules);

        Assert.True(decision.IsEligible);
        Assert.Equal("Remove 1 audio track(s) (fra) not in the kept languages — saves about 6 GB (10%)", decision.Reason);
    }

    [Fact]
    public void Track_cleanup_with_a_minimum_skips_a_file_whose_sizes_are_unknown()
    {
        var rules = Cleanup with { MinimumTrackRemovalSavingBytes = 1 * Gigabyte };

        var decision = CandidateEvaluator.Evaluate(Film(60 * Gigabyte, [null, null]), rules);

        Assert.False(decision.IsEligible);
        Assert.Contains("cannot be estimated", decision.Reason);
    }

    [Fact]
    public void A_file_with_nothing_to_remove_keeps_its_own_reason_when_a_minimum_is_set()
    {
        var rules = Cleanup with { MinimumTrackRemovalSavingPercent = 5, KeepAudioLanguages = ["eng", "fra"] };

        var decision = CandidateEvaluator.Evaluate(Film(60 * Gigabyte, null), rules);

        Assert.Equal("No removable tracks (all tracks match the kept languages or are unknown)", decision.Reason);
    }

    [Fact]
    public void Remux_cleanup_applies_the_minimum_to_a_file_already_in_its_container()
    {
        var rules = RuleProfileDefaults.For(RuleProfile.RemuxCleanup) with
        {
            KeepAudioLanguages = ["eng"],
            MinimumTrackRemovalSavingPercent = 5,
        };

        var decision = CandidateEvaluator.Evaluate(Film(60 * Gigabyte, [4 * Gigabyte, 120 * Megabyte]), rules);

        Assert.False(decision.IsEligible);
        Assert.StartsWith("Removing these tracks would save about 120 MB", decision.Reason);
    }

    [Fact]
    public void Remux_cleanup_ignores_the_minimum_when_the_container_changes()
    {
        var rules = RuleProfileDefaults.For(RuleProfile.RemuxCleanup) with
        {
            KeepAudioLanguages = ["eng"],
            MinimumTrackRemovalSavingPercent = 5,
        };

        var decision = CandidateEvaluator.Evaluate(
            Film(60 * Gigabyte, [4 * Gigabyte, 120 * Megabyte], "mov,mp4,m4a,3gp,3g2,mj2", "Films/Example.mp4"), rules);

        Assert.True(decision.IsEligible);
        Assert.StartsWith("Remux to mkv", decision.Reason);
    }

    [Fact]
    public void A_re_encode_ignores_the_minimum()
    {
        var rules = RuleProfileDefaults.For(RuleProfile.ExperimentalAv1) with
        {
            KeepAudioLanguages = ["eng"],
            MinimumTrackRemovalSavingPercent = 50,
        };

        Assert.True(CandidateEvaluator.Evaluate(Film(60 * Gigabyte, [null, null]), rules).IsEligible);
    }

    [Fact]
    public void The_minimum_applies_only_where_the_job_just_strips_tracks()
    {
        var film = Film(60 * Gigabyte, null);

        Assert.True(CandidateEvaluator.OnlyRemovesTracks(film, Cleanup));
        Assert.True(CandidateEvaluator.OnlyRemovesTracks(film, RuleProfileDefaults.For(RuleProfile.RemuxCleanup)));
        Assert.False(CandidateEvaluator.OnlyRemovesTracks(
            film with { Container = "mov,mp4,m4a,3gp,3g2,mj2", RelativePath = "Films/Example.mp4" },
            RuleProfileDefaults.For(RuleProfile.RemuxCleanup)));
        Assert.False(CandidateEvaluator.OnlyRemovesTracks(film, RuleProfileDefaults.For(RuleProfile.ConservativeHevc)));
    }

    // ---- Resolution ----

    [Fact]
    public void Track_cleanup_keeps_the_librarys_minimum_through_resolution()
    {
        var settings = RuleResolver.Resolve(RuleProfile.TrackCleanup, new RuleOverrides
        {
            MinimumTrackRemovalSavingPercent = 5,
            MinimumTrackRemovalSavingBytes = Gigabyte,
        });

        Assert.Equal(5, settings.MinimumTrackRemovalSavingPercent);
        Assert.Equal(Gigabyte, settings.MinimumTrackRemovalSavingBytes);
    }

    [Fact]
    public void Remux_cleanup_keeps_the_librarys_minimum_through_resolution() =>
        Assert.Equal(5, RuleResolver.Resolve(RuleProfile.RemuxCleanup,
            new RuleOverrides { MinimumTrackRemovalSavingPercent = 5 }).MinimumTrackRemovalSavingPercent);

    [Fact]
    public void Profiles_have_no_minimum_by_default()
    {
        foreach (var profile in Enum.GetValues<RuleProfile>())
        {
            var settings = RuleProfileDefaults.For(profile);
            Assert.Null(settings.MinimumTrackRemovalSavingPercent);
            Assert.Null(settings.MinimumTrackRemovalSavingBytes);
        }
    }

    // ---- Plan ----

    [Fact]
    public void The_plan_shows_the_estimated_saving_and_whether_it_meets_the_minimum()
    {
        var rules = Cleanup with { MinimumTrackRemovalSavingPercent = 5 };

        var plan = OptimisationPlanner.Plan(Film(60 * Gigabyte, [4 * Gigabyte, 120 * Megabyte]), rules, crf: null);

        Assert.NotNull(plan.TrackSaving);
        Assert.Equal(120 * Megabyte, plan.TrackSaving.Bytes);
        Assert.Equal(0.195, plan.TrackSaving.Percent!.Value, precision: 3);
        Assert.Equal(5, plan.TrackSaving.MinimumPercent);
        Assert.Null(plan.TrackSaving.MinimumBytes);
        Assert.False(plan.TrackSaving.MeetsMinimum);
    }

    [Fact]
    public void The_plan_shows_the_saving_without_judging_it_when_no_minimum_is_set()
    {
        var plan = OptimisationPlanner.Plan(Film(60 * Gigabyte, [4 * Gigabyte, 6 * Gigabyte]), Cleanup, crf: null);

        Assert.Equal(6 * Gigabyte, plan.TrackSaving!.Bytes);
        Assert.Null(plan.TrackSaving.MeetsMinimum);
    }

    [Fact]
    public void The_plan_says_nothing_about_a_saving_for_a_re_encode()
    {
        var rules = RuleProfileDefaults.For(RuleProfile.ExperimentalAv1) with { KeepAudioLanguages = ["eng"] };

        Assert.Null(OptimisationPlanner.Plan(Film(60 * Gigabyte, [4 * Gigabyte, 6 * Gigabyte]), rules, crf: null).TrackSaving);
    }
}
