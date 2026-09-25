using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class SizePreflightTests
{
    private const long OneGigabyte = 1_000_000_000;

    // Three 40-second windows whose source video was 100 MB each, with 20 MB of copied audio
    // alongside: 300 MB of picture out of 360 MB sampled.
    private static IReadOnlyList<IReadOnlyList<SampledStreamBytes>> Windows(
        long videoPerWindow = 100_000_000,
        long audioPerWindow = 20_000_000) =>
        Enumerable.Range(0, 3)
            .Select(_ => (IReadOnlyList<SampledStreamBytes>)[
                new SampledStreamBytes("video", 0, true, videoPerWindow),
                new SampledStreamBytes("audio", 0, false, audioPerWindow)])
            .ToList();

    private static TranscodeSpec Spec(string? audioEncoder = null, int? audioKbps = null,
        IReadOnlyList<int>? removeAudio = null) =>
        new("/in.mkv", "/out.mkv", "hevc", 24, "medium", TonemapToSdr: false, AudioEncoder: audioEncoder,
            AudioBitrateKbps: audioKbps, RemoveAudioStreamIndexes: removeAudio);

    private static SizeForecastBasis Basis(long sourceBytes = 10 * OneGigabyte, TranscodeSpec? spec = null) =>
        SizeForecastBasis.From(sourceBytes, Windows(), spec ?? Spec(), 3_600)!;

    private static AdaptiveQualityProbe Probe(long bytes, bool meets = true, int quality = 24,
        IReadOnlyList<long>? windows = null) =>
        new(quality, meets, bytes, WindowEncodedBytes: windows);

    [Fact]
    public void Samples_are_compared_with_the_source_over_the_same_scenes()
    {
        // The samples are 90% of the source's own video over the same frames. Applied to the 5/6 of
        // the file that is picture, with the copied audio unchanged, that is 91.7% of the source.
        var result = SizePreflight.Assess(Basis(), Probe(270_000_000), 10 * OneGigabyte - 1, bypass: false);

        Assert.False(result.ShouldHold);
        Assert.Equal(0.9, result.VideoRatio!.Value, 6);
        Assert.Equal(9_166_666_667, result.ProjectedBytes);
    }

    [Fact]
    public void A_sample_that_grows_the_source_video_holds_even_when_growth_is_small()
    {
        // 5% growth over the same scenes cannot save anything; the old whole-file projection
        // needed a 25% overshoot before it said a word.
        var result = SizePreflight.Assess(Basis(), Probe(315_000_000), 10 * OneGigabyte - 1, bypass: false);

        Assert.True(result.ShouldHold);
        Assert.Contains("105% of the source's own video", result.Reason);
        Assert.Contains("smaller than the source", result.Reason);
    }

    [Fact]
    public void Busy_sampled_scenes_do_not_fake_a_size_problem()
    {
        // The sampled minutes are twice as dense as the title's average. Projecting the sample by
        // duration would call a 60% encode oversized; the same-scene ratio does not.
        var dense = SizeForecastBasis.From(
            10 * OneGigabyte, Windows(videoPerWindow: 400_000_000, audioPerWindow: 20_000_000), Spec(), 3_600)!;

        var result = SizePreflight.Assess(dense, Probe(720_000_000), 10 * OneGigabyte - 1, bypass: false);

        Assert.False(result.ShouldHold);
        Assert.Equal(0.6, result.VideoRatio!.Value, 6);
    }

    [Fact]
    public void Minimum_saving_target_is_the_limit()
    {
        // 95% of the video plus unchanged audio is 95.8% of the source; a 10% saving needs 90%.
        var result = SizePreflight.Assess(Basis(), Probe(285_000_000), 9 * OneGigabyte, bypass: false);

        Assert.True(result.ShouldHold);
        Assert.Contains("at most 90% of the source", result.Reason);
    }

    [Fact]
    public void Removed_audio_tracks_are_not_counted_as_carried()
    {
        var withoutAudio = Basis(spec: Spec(removeAudio: [0]));

        Assert.Equal(0, withoutAudio.CarriedShare);
        var result = SizePreflight.Assess(withoutAudio, Probe(315_000_000), 10 * OneGigabyte - 1, bypass: false);
        // 105% of the picture share (5/6) is 87.5% of the source once the audio is gone.
        Assert.False(result.ShouldHold);
    }

    [Fact]
    public void Reencoded_audio_is_estimated_from_its_target_bitrate()
    {
        var basis = Basis(spec: Spec(audioEncoder: "libopus", audioKbps: 128));

        Assert.Equal(0, basis.CarriedShare);
        // One kept track at 128 kb/s for an hour.
        Assert.Equal(57_600_000, basis.ReencodedAudioBytes);
    }

    [Fact]
    public void Per_window_ratios_are_reported_when_the_worker_sent_them()
    {
        var result = SizePreflight.Assess(Basis(),
            Probe(330_000_000, windows: [100_000_000, 110_000_000, 120_000_000]),
            10 * OneGigabyte - 1, bypass: false);

        Assert.Equal([1.0, 1.1, 1.2], result.WindowRatios!.Select(r => Math.Round(r, 3)));
        Assert.Contains("100%, 110%, 120% across the 3 samples", result.Reason);
    }

    [Fact]
    public void Explicit_approval_bypasses_the_estimate_without_weakening_final_gate()
    {
        var result = SizePreflight.Assess(Basis(), Probe(500_000_000), 10 * OneGigabyte - 1, bypass: true);

        Assert.False(result.ShouldHold);
    }

    [Fact]
    public void Without_a_size_gate_or_evidence_nothing_is_held()
    {
        Assert.False(SizePreflight.Assess(Basis(), Probe(500_000_000), null, bypass: false).ShouldHold);
        Assert.False(SizePreflight.Assess(null, Probe(500_000_000), 10 * OneGigabyte - 1, false).ShouldHold);
        Assert.False(SizePreflight.Assess(Basis(), null, 10 * OneGigabyte - 1, false).ShouldHold);
        Assert.False(SizePreflight.Assess(Basis(), Probe(0), 10 * OneGigabyte - 1, false).ShouldHold);
    }

    [Fact]
    public void A_basis_needs_picture_bytes_in_every_window()
    {
        IReadOnlyList<IReadOnlyList<SampledStreamBytes>> missing =
        [
            [new SampledStreamBytes("video", 0, true, 100)],
            [new SampledStreamBytes("audio", 0, false, 100)]
        ];

        Assert.Null(SizeForecastBasis.From(OneGigabyte, missing, Spec(), 3_600));
        Assert.Null(SizeForecastBasis.From(0, Windows(), Spec(), 3_600));
    }

    [Fact]
    public void A_candidate_that_misses_vmaf_and_still_does_not_fit_ends_the_search_early()
    {
        var failing = Probe(330_000_000, meets: false, quality: 24);
        var decision = AdaptiveQualitySearch.Decide(24, [failing]);
        Assert.False(decision.Complete);

        var review = SizePreflight.Review(Basis(), [failing], failing, decision, 10 * OneGigabyte - 1, bypass: false);

        Assert.True(review.ShouldHold);
        Assert.Contains("missed the VMAF target", review.Reason);
        Assert.Contains("search stopped here", review.Reason);
    }

    [Fact]
    public void A_candidate_that_passes_vmaf_keeps_searching_even_when_it_is_too_large()
    {
        // A higher quality value may still clear the target at a smaller size.
        var passing = Probe(330_000_000, meets: true, quality: 24);
        var decision = AdaptiveQualitySearch.Decide(24, [passing]);
        Assert.False(decision.Complete);

        var review = SizePreflight.Review(Basis(), [passing], passing, decision, 10 * OneGigabyte - 1, bypass: false);

        Assert.False(review.ShouldHold);
    }

    [Fact]
    public void A_failing_candidate_that_fits_keeps_searching()
    {
        var failing = Probe(240_000_000, meets: false, quality: 24);
        var decision = AdaptiveQualitySearch.Decide(24, [failing]);

        var review = SizePreflight.Review(Basis(), [failing], failing, decision, 10 * OneGigabyte - 1, bypass: false);

        Assert.False(review.ShouldHold);
    }

    [Fact]
    public void A_finished_search_is_judged_on_the_quality_it_chose()
    {
        IReadOnlyList<AdaptiveQualityProbe> probes =
        [
            Probe(330_000_000, meets: true, quality: 24),
            Probe(310_000_000, meets: true, quality: 30),
            Probe(305_000_000, meets: true, quality: 42),
            Probe(300_500_000, meets: true, quality: 51)
        ];
        var decision = AdaptiveQualitySearch.Decide(24, probes);
        Assert.True(decision.Complete);
        Assert.Equal(51, decision.SelectedQuality);

        var review = SizePreflight.Review(Basis(), probes, probes[^1], decision, 10 * OneGigabyte - 1, bypass: false);

        Assert.True(review.ShouldHold);
        Assert.Contains("At quality 51 the samples cleared the VMAF target", review.Reason);
    }
}
