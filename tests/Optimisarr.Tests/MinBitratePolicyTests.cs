using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

/// <summary>
/// The bitrate floor from #95, added alongside the cap that already shipped. A floor only means
/// something inside a VBV window: x264 and x265 reject or ignore -minrate without -maxrate and
/// -bufsize, so a floor is emitted only when a cap is set, and the pair must be ordered.
/// </summary>
public sealed class MinBitratePolicyTests
{
    private static IReadOnlyList<string> Resolve(string encoder, int? min, int? max) =>
        EncoderTuningPolicy.Resolve(encoder, new EncoderTuning(MaxBitrateKbps: max, MinBitrateKbps: min));

    [Fact]
    public void A_floor_is_emitted_alongside_a_cap_on_x26x()
    {
        var args = Resolve("libx265", min: 2000, max: 8000);

        Assert.Equal("2000k", args[args.ToList().IndexOf("-minrate") + 1]);
        Assert.Equal("8000k", args[args.ToList().IndexOf("-maxrate") + 1]);
        Assert.Contains("-bufsize", args);
    }

    [Fact]
    public void A_floor_without_a_cap_is_dropped_rather_than_sent_without_its_vbv_window()
    {
        // -minrate on its own is not a constraint the encoder can honour under constant quality.
        // Sending it would look applied while doing nothing, which is the failure this policy
        // exists to prevent.
        Assert.Empty(Resolve("libx265", min: 2000, max: null));
    }

    [Fact]
    public void The_floor_does_not_reach_nvenc_even_though_the_cap_does()
    {
        // nvenc.c reads rc_max_rate for its VBR ceiling but never rc_min_rate. Emitting -minrate
        // there would look applied while doing nothing — the exact failure this policy refuses
        // elsewhere — so NVENC keeps its cap and gets no floor.
        var args = Resolve("hevc_nvenc", min: 1500, max: 6000);

        Assert.DoesNotContain("-minrate", args);
        Assert.Contains("-maxrate", args);
    }

    [Fact]
    public void The_floor_is_dropped_where_the_cap_is_dropped()
    {
        // The cap does not reach QSV, VAAPI or SVT-AV1 (constant-quantiser modes, or a different
        // control), and a floor is meaningless without it.
        Assert.Empty(Resolve("hevc_qsv", min: 1500, max: 6000));
        Assert.Empty(Resolve("hevc_vaapi", min: 1500, max: 6000));
        Assert.Empty(Resolve("libsvtav1", min: 1500, max: 6000));
    }

    [Fact]
    public void A_non_positive_floor_is_treated_as_no_floor()
    {
        var args = Resolve("libx265", min: 0, max: 8000);

        Assert.DoesNotContain("-minrate", args);
        Assert.Contains("-maxrate", args);
    }

    [Fact]
    public void A_floor_above_the_cap_is_never_emitted()
    {
        // The parser refuses this at the boundary; the policy refuses it too so a stored pair that
        // somehow got past validation cannot hand the encoder an impossible window.
        var args = Resolve("libx265", min: 9000, max: 8000);

        Assert.DoesNotContain("-minrate", args);
        Assert.Contains("-maxrate", args);
    }

    [Fact]
    public void A_floor_alone_keeps_the_tuning_from_reading_as_empty()
    {
        // IsEmpty short-circuits the whole resolve. A floor is a real request even when the policy
        // then declines it for want of a cap; the emptiness check must not hide it.
        Assert.False(new EncoderTuning(MinBitrateKbps: 2000).IsEmpty);
    }
}
