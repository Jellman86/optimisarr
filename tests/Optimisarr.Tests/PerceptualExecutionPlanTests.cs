using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class PerceptualExecutionPlanTests
{
    private static readonly string Source = new('a', 64);
    private static readonly VerificationPolicy Policy = VerificationPolicy.Default with
    {
        ImagePerceptualGateEnabled = true, MinimumImagePerceptualScore = 85,
        AudioQualityGateEnabled = true, MaximumAudioQualityDistance = 0.02
    };
    private static PerceptualSearchSummary Previous(bool image) => new(image ? MediaKind.Image : MediaKind.Audio,
        image ? "libwebp" : "aac", Source, 85, 0.02, [],
        new([new(image ? 80 : 128, 1200, new(true, true, 90, null)), new(image ? 60 : 96, 1000, new(true, true, 86, null))],
            new(null, image ? 60 : 96, false, "previous host")), RetryValue: image ? 70 : 112);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_retry_measures_again_on_the_current_encoder_and_cannot_borrow_the_previous_hosts_passing_probes(bool image)
    {
        var previous = Previous(image);
        var plan = PerceptualExecutionPlan.Create(previous.Kind, previous.Encoder, image ? 80 : 128, Policy, previous, Source, 1);
        var measured = new List<int>();
        var result = await PerceptualSearchRunner.RunAsync(plan, (value, index, _) =>
        {
            measured.Add(value);
            return Task.FromResult(new PerceptualSearchProbe(value, value * 10, new(true, index > 0, index > 0 ? 90 : 50, null)));
        }, default);
        Assert.NotEmpty(measured);
        Assert.Equal(plan.Baseline, measured[0]);
        Assert.All(measured, value => Assert.True(value >= previous.RetryValue));
        Assert.All(result.Probes, probe => Assert.Contains(probe.Value, measured));
        Assert.False(result.Probes[0].Measurement.MeetsTarget);
        Assert.True(result.Decision.SelectedValue > plan.Baseline);
        Assert.Equal(previous.RetryValue, plan.MinimumValue);
        Assert.False(result.Decision.FellBack);
    }

    [Fact]
    public async Task An_unavailable_retry_uses_its_higher_fidelity_minimum_instead_of_the_old_library_setting()
    {
        var previous = Previous(false) with { RetryValue = 224 };
        var plan = PerceptualExecutionPlan.Create(MediaKind.Audio, "aac", 128, Policy, previous, Source, 1);
        var result = await PerceptualSearchRunner.RunAsync(plan, (value, _, _) => Task.FromResult(
            new PerceptualSearchProbe(value, 0, PerceptualSearchMeasurement.Unavailable("current scorer unavailable"))), default);
        Assert.Equal(224, result.Decision.SelectedValue); Assert.True(result.Decision.FellBack);
        Assert.Contains("retry", result.Decision.Reason);
        Assert.Contains("current scorer unavailable", result.Decision.Reason);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("encoder")]
    [InlineData("target")]
    [InlineData("gate")]
    [InlineData("floor")]
    [InlineData("kind")]
    public void Changed_retry_identity_or_policy_cannot_silently_restart_at_an_unrelated_saved_setting(string change)
    {
        var previous = Previous(true);
        var policy = Policy;
        if (change == "target") policy = policy with { MinimumImagePerceptualScore = 90 };
        if (change == "gate") policy = policy with { ImagePerceptualGateEnabled = false };
        if (change == "floor") previous = previous with { RetryValue = 50 };
        if (change == "kind") previous = previous with { Kind = MediaKind.Audio };
        Assert.Throws<ArgumentException>(() => PerceptualExecutionPlan.Create(MediaKind.Image,
            change == "encoder" ? "mjpeg" : "libwebp", 80, policy, previous, change == "source" ? new('b', 64) : Source, 1));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(2, true)]
    public void Lost_stale_or_exhausted_retry_intent_cannot_restore_an_unconstrained_search(int retries, bool retained)
    {
        Assert.Throws<ArgumentException>(() => PerceptualExecutionPlan.Create(MediaKind.Image, "libwebp", 80,
            Policy, retained ? Previous(true) : null, Source, retries));
    }

    [Fact]
    public void An_unrelated_audio_target_change_does_not_discard_an_image_retry_minimum()
    {
        var plan = PerceptualExecutionPlan.Create(MediaKind.Image, "libwebp", 80,
            Policy with { MaximumAudioQualityDistance = 0.01 }, Previous(true), Source, 1);
        Assert.Equal(70, plan.MinimumValue);
    }
}
