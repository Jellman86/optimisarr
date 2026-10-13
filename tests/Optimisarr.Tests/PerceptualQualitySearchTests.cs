using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class PerceptualQualitySearchTests
{
    [Fact]
    public void Image_ladder_keeps_the_baseline_and_runs_from_best_to_most_compressed()
    {
        var plan = PerceptualQualityPlan.Image("libwebp", 80);
        Assert.Equal(80, plan.Values[plan.BaselineIndex]);
        Assert.Equal(100, plan.Values[0]);
        Assert.Equal(0, plan.Values[^1]);
        Assert.Equal(plan.Values.OrderDescending(), plan.Values);
        Assert.Equal(plan.Values.Count, plan.Values.Distinct().Count());
    }

    [Fact]
    public void Jpeg_ladder_never_spends_two_probes_on_the_same_encoder_quantizer()
    {
        var plan = PerceptualQualityPlan.Image("mjpeg", 81);
        var quantizers = plan.Values.Select(q => FfmpegCommandBuilder.Build(new("in.png", "out.jpg", null, null, null, false,
            Kind: MediaKind.Image, ImageEncoder: "mjpeg", ImageQuality: q)))
            .Select(args => args[args.ToList().IndexOf("-q:v") + 1]).ToArray();
        Assert.Equal(81, plan.Values[plan.BaselineIndex]);
        Assert.Equal(quantizers.Length, quantizers.Distinct().Count());
    }

    [Theory]
    [InlineData("aac", 128, 320, 48)]
    [InlineData("libopus", 160, 256, 48)]
    [InlineData("libmp3lame", 192, 320, 64)]
    public void Audio_search_uses_the_encoder_bitrate_ladder(string encoder, int baseline, int best, int smallest)
    {
        var plan = PerceptualQualityPlan.Audio(encoder, baseline);
        Assert.Equal(baseline, plan.Values[plan.BaselineIndex]);
        Assert.Equal(best, plan.Values[0]);
        Assert.Equal(smallest, plan.Values[^1]);
    }

    [Fact]
    public void Custom_audio_baseline_is_retained_without_assuming_an_equivalent_preset()
    {
        var plan = PerceptualQualityPlan.Audio("libopus", 137);
        Assert.Equal(137, plan.Values[plan.BaselineIndex]);
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("flac")]
    [InlineData("unknown")]
    public void Unsupported_audio_encoder_has_no_search(string encoder) =>
        Assert.Throws<ArgumentException>(() => PerceptualQualityPlan.Audio(encoder, 128));

    [Fact]
    public void Search_measures_baseline_then_tries_more_compression()
    {
        var plan = PerceptualQualityPlan.Image("libwebp", 80);
        var first = PerceptualQualitySearch.Decide(plan, []);
        Assert.Equal(80, first.NextValue);
        var next = PerceptualQualitySearch.Decide(plan, [new(80, true, 1000)]);
        Assert.True(next.NextValue < 80);
    }

    [Fact]
    public void A_failed_baseline_reaches_the_highest_quality_to_find_a_passing_anchor()
    {
        var plan = PerceptualQualityPlan.Audio("aac", 128);
        var next = PerceptualQualitySearch.Decide(plan, [new(128, false, 1000)]);
        Assert.Equal(320, next.NextValue);
    }

    [Fact]
    public void Selection_uses_actual_bytes_even_if_the_encoder_size_order_is_surprising()
    {
        var plan = PerceptualQualityPlan.Audio("aac", 128);
        var result = PerceptualQualitySearch.Decide(plan, [new(128, true, 900), new(96, true, 1000)]);
        Assert.True(result.Complete);
        Assert.Equal(128, result.SelectedValue);
        Assert.False(result.FellBack);
    }

    [Fact]
    public void Contradictory_results_fall_back_with_a_reason_and_still_require_final_verification()
    {
        var plan = PerceptualQualityPlan.Audio("aac", 128);
        var result = PerceptualQualitySearch.Decide(plan, [new(128, false, 1000), new(96, true, 700)]);
        Assert.True(result.Complete);
        Assert.True(result.FellBack);
        Assert.Equal(128, result.SelectedValue);
        Assert.Contains("non-monotonic", result.Reason);
    }

    [Fact]
    public void Missing_passes_never_get_reported_as_a_quality_success()
    {
        var plan = PerceptualQualityPlan.Audio("aac", 128);
        var result = PerceptualQualitySearch.Decide(plan, [new(128, false, 1000), new(320, false, 2000)]);
        Assert.True(result.Complete);
        Assert.True(result.FellBack);
        Assert.Equal(128, result.SelectedValue);
    }

    [Fact]
    public void Search_terminates_without_repeating_a_candidate_for_every_possible_passing_boundary()
    {
        foreach (var plan in new[] { PerceptualQualityPlan.Image("libwebp", 80), PerceptualQualityPlan.Image("mjpeg", 80), PerceptualQualityPlan.Audio("libopus", 128) })
        for (var lastPassingIndex = -1; lastPassingIndex < plan.Values.Count; lastPassingIndex++)
        {
            var probes = new List<PerceptualQualityProbe>();
            while (true)
            {
                var decision = PerceptualQualitySearch.Decide(plan, probes);
                if (decision.Complete)
                {
                    Assert.True(probes.Count <= PerceptualQualitySearch.MaximumProbes);
                    Assert.True(decision.FellBack || probes.Any(p => p.Value == decision.SelectedValue && p.MeetsTarget));
                    break;
                }
                var value = decision.NextValue!.Value;
                Assert.DoesNotContain(probes, p => p.Value == value);
                var index = plan.Values.ToList().IndexOf(value);
                probes.Add(new(value, index <= lastPassingIndex, 10000 - index * 100));
                Assert.True(probes.Count <= PerceptualQualitySearch.MaximumProbes);
            }
        }
    }

    [Fact]
    public void Malformed_size_or_unplanned_values_cannot_guide_a_search()
    {
        var plan = PerceptualQualityPlan.Audio("aac", 128);
        Assert.Throws<ArgumentException>(() => PerceptualQualitySearch.Decide(plan, [new(129, true, 100)]));
        Assert.Throws<ArgumentException>(() => PerceptualQualitySearch.Decide(plan, [new(128, true, 0)]));
        Assert.Throws<ArgumentException>(() => PerceptualQualitySearch.Decide(plan, [new(128, true, -1)]));
    }

    [Fact]
    public void Repeated_reports_do_not_spend_the_probe_budget_twice()
    {
        var plan = PerceptualQualityPlan.Audio("aac", 128);
        var probe = new PerceptualQualityProbe(128, true, 1000);
        Assert.Equal(PerceptualQualitySearch.Decide(plan, [probe]), PerceptualQualitySearch.Decide(plan, [probe, probe]));
    }
}
