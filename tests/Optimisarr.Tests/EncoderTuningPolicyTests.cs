using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

/// <summary>
/// The advanced encoder knobs, resolved the way encoder effort already is: the library stores a
/// portable intent, and the exact encoder chosen at dispatch receives only arguments it actually
/// supports. Auto mode means a library cannot know whether it will land on x265 or NVENC, so a
/// setting that named one encoder's flags directly would be a setting that silently does nothing
/// half the time.
///
/// Where a family has no equivalent the answer is no arguments, never an approximation. A knob
/// that quietly did something else would be worse than one that did nothing, because the operator
/// would believe the encode honoured it.
/// </summary>
public sealed class EncoderTuningPolicyTests
{
    private static IReadOnlyList<string> Resolve(
        string encoder,
        ContentTune tune = ContentTune.None,
        int? maxBitrateKbps = null,
        bool strongerAdaptiveQuantisation = false) =>
        EncoderTuningPolicy.Resolve(
            encoder,
            new EncoderTuning(tune, maxBitrateKbps, strongerAdaptiveQuantisation));

    [Fact]
    public void Nothing_is_added_when_no_knob_is_set()
    {
        // The default for every library. An encode must be byte-for-byte the command it was
        // before this existed unless the operator asked for something.
        Assert.Empty(Resolve("libx265"));
        Assert.Empty(Resolve("hevc_nvenc"));
        Assert.Empty(Resolve("hevc_qsv"));
        Assert.Empty(Resolve("hevc_vaapi"));
        Assert.Empty(Resolve("libsvtav1"));
    }

    // --- Content tune -------------------------------------------------------------------------

    [Fact]
    public void Animation_tune_reaches_the_x26x_encoders()
    {
        // The reporter's actual ask: better quality on anime and cartoons. x264 and x265 are the
        // only encoders Optimisarr ships that understand content tuning at all.
        Assert.Equal(["-tune", "animation"], Resolve("libx265", tune: ContentTune.Animation));
        Assert.Equal(["-tune", "animation"], Resolve("libx264", tune: ContentTune.Animation));
    }

    [Fact]
    public void Grain_tune_reaches_the_x26x_encoders()
    {
        Assert.Equal(["-tune", "grain"], Resolve("libx265", tune: ContentTune.Grain));
    }

    [Fact]
    public void A_content_tune_is_dropped_for_encoders_that_have_no_such_idea()
    {
        // NVENC's own -tune selects a latency profile (hq/ll/ull), not a content type. Passing
        // "animation" there is not a worse tune, it is a different question — and would fail the
        // encode outright. QSV and VAAPI have no equivalent either.
        Assert.Empty(Resolve("hevc_nvenc", tune: ContentTune.Animation));
        Assert.Empty(Resolve("hevc_qsv", tune: ContentTune.Animation));
        Assert.Empty(Resolve("hevc_vaapi", tune: ContentTune.Animation));
        Assert.Empty(Resolve("libsvtav1", tune: ContentTune.Animation));
    }

    // --- Maximum bitrate ----------------------------------------------------------------------

    [Fact]
    public void A_bitrate_cap_reaches_the_encoders_where_capped_quality_is_a_real_mode()
    {
        // x264/x265 pair -crf with -maxrate as documented "capped CRF"; NVENC is already on VBR
        // with no target bitrate, so a cap is exactly the ceiling it is missing.
        foreach (var encoder in new[] { "libx264", "libx265", "hevc_nvenc" })
        {
            var args = Resolve(encoder, maxBitrateKbps: 8000);

            Assert.Contains("-maxrate", args);
            Assert.Contains("8000k", args);
        }
    }

    [Fact]
    public void A_bitrate_cap_is_dropped_where_it_would_be_ignored_or_change_the_rate_control_mode()
    {
        // QSV and VAAPI run constant-quantiser modes here (-global_quality and -rc_mode CQP), where
        // a rate cap is at best ignored and at worst quietly moves the encoder onto a different
        // rate-control mode than the quality setting chose. SVT-AV1's capped-CRF control is its own
        // parameter, not -maxrate. Passing an argument that does nothing is the exact failure this
        // policy exists to prevent, so these keep their own rate control.
        Assert.Empty(Resolve("hevc_qsv", maxBitrateKbps: 8000));
        Assert.Empty(Resolve("hevc_vaapi", maxBitrateKbps: 8000));
        Assert.Empty(Resolve("libsvtav1", maxBitrateKbps: 8000));
    }

    [Fact]
    public void A_bitrate_cap_always_carries_a_buffer_size()
    {
        // -maxrate without -bufsize is ignored by x264/x265 rather than honoured, which would make
        // the setting look applied while doing nothing. Two seconds of the cap is the usual choice.
        var args = Resolve("libx265", maxBitrateKbps: 8000);

        var bufsize = args.ToList().IndexOf("-bufsize");
        Assert.True(bufsize >= 0, "a cap must always be paired with a buffer size");
        Assert.Equal("16000k", args[bufsize + 1]);
    }

    [Fact]
    public void A_cap_of_zero_or_less_is_treated_as_no_cap()
    {
        // Rather than emitting "-maxrate 0k", which pins the encoder to an impossible target.
        Assert.Empty(Resolve("libx265", maxBitrateKbps: 0));
        Assert.Empty(Resolve("libx265", maxBitrateKbps: -1));
    }

    // --- Adaptive quantisation ----------------------------------------------------------------

    [Fact]
    public void Stronger_adaptive_quantisation_uses_each_familys_own_control()
    {
        // Same intent — spend more bits where the eye notices — expressed in two vocabularies.
        Assert.Equal(
            ["-x265-params", "aq-mode=3"],
            Resolve("libx265", strongerAdaptiveQuantisation: true));

        Assert.Equal(
            ["-x264-params", "aq-mode=3"],
            Resolve("libx264", strongerAdaptiveQuantisation: true));

        Assert.Equal(
            ["-spatial-aq", "1", "-temporal-aq", "1"],
            Resolve("hevc_nvenc", strongerAdaptiveQuantisation: true));
    }

    [Fact]
    public void Adaptive_quantisation_is_dropped_where_it_cannot_be_expressed()
    {
        Assert.Empty(Resolve("hevc_qsv", strongerAdaptiveQuantisation: true));
        Assert.Empty(Resolve("hevc_vaapi", strongerAdaptiveQuantisation: true));
        Assert.Empty(Resolve("libsvtav1", strongerAdaptiveQuantisation: true));
    }

    // --- Combinations -------------------------------------------------------------------------

    [Fact]
    public void Several_knobs_combine_without_colliding()
    {
        var args = Resolve(
            "libx265", tune: ContentTune.Animation, maxBitrateKbps: 6000,
            strongerAdaptiveQuantisation: true);

        Assert.Equal(
            ["-tune", "animation", "-maxrate", "6000k", "-bufsize", "12000k", "-x265-params", "aq-mode=3"],
            args);
    }

    [Fact]
    public void An_unknown_encoder_receives_nothing_rather_than_a_guess()
    {
        // Fails closed the way the capability matcher does: a family this policy has never been
        // taught cannot be assumed to share anyone else's spelling.
        Assert.Empty(Resolve("some_future_encoder", tune: ContentTune.Animation, maxBitrateKbps: 5000,
            strongerAdaptiveQuantisation: true));
    }
}
