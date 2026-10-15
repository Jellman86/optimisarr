using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class PerceptualRetryFloorTests
{
    [Fact]
    public void A_retry_on_a_new_host_starts_at_or_above_its_required_fidelity()
    {
        var plan = PerceptualQualityPlan.Audio("aac", 128, 224);
        Assert.Equal(224, plan.Baseline);
        Assert.Equal([320, 256, 224], plan.Values);
        Assert.Equal(224, plan.MinimumValue);
        var first = PerceptualQualitySearch.Decide(plan, []);
        Assert.Equal(224, first.NextValue);
        var result = PerceptualQualitySearch.Decide(plan, [new(224, true, 1000)]);
        Assert.True(result.Complete);
        Assert.Equal(224, result.SelectedValue);
        Assert.DoesNotContain(128, plan.Values);
    }

    [Fact]
    public void A_jpeg_retry_at_the_highest_quantizer_measures_once_and_does_not_encode_an_identical_second_setting()
    {
        var plan = PerceptualQualityPlan.Image("mjpeg", 80, 99);
        Assert.Equal(99, Assert.Single(plan.Values));
        Assert.Equal(99, PerceptualQualitySearch.Decide(plan, []).NextValue);
        var passed = PerceptualQualitySearch.Decide(plan, [new(99, true, 1000)]);
        Assert.True(passed.Complete);
        Assert.False(passed.FellBack);
        Assert.Equal(99, passed.SelectedValue);
        var failed = PerceptualQualitySearch.Decide(plan, [new(99, false, 1000)]);
        Assert.True(failed.Complete);
        Assert.True(failed.FellBack);
        Assert.Equal(99, failed.SelectedValue);
    }

    [Fact]
    public async Task An_unavailable_retry_sample_falls_back_to_the_retry_floor_without_erasing_why_it_stopped()
    {
        var plan = PerceptualQualityPlan.Image("libwebp", 80, 90);
        var result = await PerceptualSearchRunner.RunAsync(plan, (value, _, _) =>
            Task.FromResult(new PerceptualSearchProbe(value, 0, PerceptualSearchMeasurement.Unavailable("scorer timed out"))), default);
        Assert.True(result.Decision.FellBack);
        Assert.Equal(90, result.Decision.SelectedValue);
        Assert.Contains("retry", result.Decision.Reason);
        Assert.Contains("timed out", result.Decision.Reason);
        Assert.Single(result.Probes);
    }

    [Fact]
    public void A_newer_library_baseline_can_search_down_to_a_supported_custom_aac_floor()
    {
        var plan = PerceptualQualityPlan.Audio("aac", 256, 200);
        Assert.Equal(256, plan.Baseline);
        Assert.Equal(200, plan.Values[^1]);
        Assert.DoesNotContain(plan.Values, value => value < 200);
        Assert.Throws<ArgumentException>(() => PerceptualQualityPlan.Audio("libmp3lame", 128, 123));
        Assert.Throws<ArgumentOutOfRangeException>(() => PerceptualQualityPlan.Audio("aac", 128, 400));
        Assert.Throws<ArgumentOutOfRangeException>(() => PerceptualQualityPlan.Image("libwebp", 80, 101));
    }
}
