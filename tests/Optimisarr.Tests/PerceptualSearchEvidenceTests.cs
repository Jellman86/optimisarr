using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class PerceptualSearchEvidenceTests
{
    private const string Source = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Candidate = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Theory]
    [InlineData(80, true)]
    [InlineData(79.999, false)]
    public void Image_target_uses_the_same_raw_inclusive_gate_as_final_verification(double score, bool passes)
    {
        var measured = PerceptualSearchEvidence.Image(Report(score), Source, Candidate, 80);
        Assert.True(measured.Available);
        Assert.Equal(passes, measured.MeetsTarget);
        Assert.Equal(score, measured.Score);
    }

    [Fact]
    public void Alpha_damage_is_a_failed_probe_even_when_the_colour_score_passes()
    {
        var report = Report(99) with { Measurement = new(32, 32, true, 0.01, 99, [99, 99]) };
        var measured = PerceptualSearchEvidence.Image(report, Source, Candidate, 80);
        Assert.True(measured.Available);
        Assert.False(measured.MeetsTarget);
    }

    [Fact]
    public void Image_evidence_cannot_be_reused_for_another_candidate_or_source()
    {
        Assert.False(PerceptualSearchEvidence.Image(Report(90), Candidate, Candidate, 80).Available);
        Assert.False(PerceptualSearchEvidence.Image(Report(90), Source, Source, 80).Available);
        Assert.False(PerceptualSearchEvidence.Image(Report(90) with { Error = "failed" }, Source, Candidate, 80).Available);
        Assert.False(PerceptualSearchEvidence.Image(Report(90) with { MetricSha256 = null }, Source, Candidate, 80).Available);
        Assert.False(PerceptualSearchEvidence.Image(Report(90) with { Measurement = null }, Source, Candidate, 80).Available);
    }

    [Theory]
    [InlineData(0.02, true)]
    [InlineData(0.020001, false)]
    public void Audio_uses_the_worst_channel_without_averaging_damage_away(double rightDistance, bool passes)
    {
        var result = PerceptualSearchEvidence.Audio(Audio(rightDistance), Source, Candidate, 0.02);
        Assert.True(result.Available);
        Assert.Equal(passes, result.MeetsTarget);
        Assert.Equal(rightDistance, result.Score);
    }

    [Fact]
    public void Missing_audio_windows_or_tool_hashes_cannot_guide_the_search()
    {
        var audio = Audio(0.01);
        var assessment = audio.Evidence!.Assessment;
        foreach (var broken in new[] { assessment with { Windows = [] }, assessment with { MetricSha256 = null },
            assessment with { FfmpegSha256 = null }, assessment with { ReferenceSha256 = Candidate },
            assessment with { Measured = false }, assessment with { Windows = [new(new(0, 30), new(1440000, [0.01]))] } })
        {
            var report = audio with { Evidence = RemoteAudioQualityEvidence.From(broken) };
            Assert.False(PerceptualSearchEvidence.Audio(report, Source, Candidate, 0.02).Available);
        }
    }

    [Fact]
    public void A_missing_or_invalid_target_is_unavailable_instead_of_silently_passing()
    {
        Assert.False(PerceptualSearchEvidence.Image(Report(90), Source, Candidate, null).Available);
        Assert.False(PerceptualSearchEvidence.Image(Report(90), Source, Candidate, double.NaN).Available);
        Assert.False(PerceptualSearchEvidence.Audio(Audio(0.01), Source, Candidate, null).Available);
        Assert.False(PerceptualSearchEvidence.Audio(Audio(0.01), Source, Candidate, double.NaN).Available);
    }

    [Fact]
    public async Task Runner_keeps_measured_evidence_and_never_exceeds_the_budget()
    {
        var plan = PerceptualQualityPlan.Image("libwebp", 80);
        var asked = new List<int>();
        var result = await PerceptualSearchRunner.RunAsync(plan, async (value, index, token) =>
        {
            asked.Add(value);
            await Task.Yield();
            return new(value, 1000 + value, PerceptualSearchEvidence.Image(Report(value), Source, Candidate, 60));
        }, CancellationToken.None);
        Assert.True(asked.Count <= PerceptualQualitySearch.MaximumProbes);
        Assert.Equal(asked, result.Probes.Select(p => p.Value));
        Assert.False(result.Decision.FellBack);
        Assert.Contains(result.Probes, p => p.Value == result.Decision.SelectedValue && p.Measurement.MeetsTarget);
    }

    [Fact]
    public async Task Unavailable_evidence_preserves_the_failure_reason_and_stops_searching()
    {
        var calls = 0;
        var result = await PerceptualSearchRunner.RunAsync(PerceptualQualityPlan.Audio("aac", 128), (value, index, token) =>
        {
            calls++;
            return Task.FromResult(new PerceptualSearchProbe(value, 500, PerceptualSearchMeasurement.Unavailable("metric missing")));
        }, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.True(result.Decision.FellBack);
        Assert.Equal(128, result.Decision.SelectedValue);
        Assert.Contains("metric missing", result.Decision.Reason);
        Assert.Single(result.Probes);
    }

    [Fact]
    public async Task Cancelled_search_never_becomes_a_fallback_success()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PerceptualSearchRunner.RunAsync(
            PerceptualQualityPlan.Image("libwebp", 80), (value, index, token) =>
            {
                calls++;
                cancellation.Cancel();
                return Task.FromResult(new PerceptualSearchProbe(value, 500, PerceptualSearchEvidence.Image(Report(90), Source, Candidate, 80)));
            }, cancellation.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Runner_refuses_a_measurement_for_a_different_setting()
    {
        var result = await PerceptualSearchRunner.RunAsync(PerceptualQualityPlan.Image("libwebp", 80),
            (value, index, token) => Task.FromResult(new PerceptualSearchProbe(value - 1, 1000,
                PerceptualSearchEvidence.Image(Report(90), Source, Candidate, 80))), CancellationToken.None);
        Assert.True(result.Decision.FellBack);
        Assert.Empty(result.Probes);
    }

    [Fact]
    public async Task Completed_probe_snapshots_survive_cancellation_of_a_later_probe()
    {
        using var cancellation = new CancellationTokenSource();
        var retained = new List<PerceptualSearchResult>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PerceptualSearchRunner.RunAsync(
            PerceptualQualityPlan.Image("libwebp", 80), (value, index, token) =>
            {
                if (index == 1) cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new PerceptualSearchProbe(value, 1000,
                    PerceptualSearchEvidence.Image(Report(90), Source, Candidate, 80)));
            }, cancellation.Token, (snapshot, token) => { retained.Add(snapshot); return Task.CompletedTask; }));
        var first = Assert.Single(retained);
        Assert.Single(first.Probes);
        Assert.Equal(80, first.Probes[0].Value);
        Assert.NotNull(first.Decision.NextValue);
        Assert.False(first.Decision.Complete);
    }

    [Fact]
    public async Task Unavailable_measurements_are_retained_with_a_settled_fallback()
    {
        var retained = new List<PerceptualSearchResult>();
        var result = await PerceptualSearchRunner.RunAsync(PerceptualQualityPlan.Audio("aac", 128),
            (value, index, token) => Task.FromResult(new PerceptualSearchProbe(value, 0,
                PerceptualSearchMeasurement.Unavailable("encoder failed"))), CancellationToken.None,
            (snapshot, token) => { retained.Add(snapshot); return Task.CompletedTask; });
        Assert.Equal(result, Assert.Single(retained));
        Assert.True(result.Decision.Complete);
        Assert.True(result.Decision.FellBack);
    }

    [Fact]
    public async Task An_expected_process_failure_keeps_prior_measured_probes_and_the_actual_failure()
    {
        var result = await PerceptualSearchRunner.RunAsync(PerceptualQualityPlan.Image("libwebp", 80),
            (value, index, token) => index == 0
                ? Task.FromResult(new PerceptualSearchProbe(value, 1000, PerceptualSearchEvidence.Image(Report(90), Source, Candidate, 80)))
                : throw new IOException("scratch disk unavailable"), CancellationToken.None);
        Assert.Equal(2, result.Probes.Count);
        Assert.True(result.Probes[0].Measurement.Available);
        Assert.False(result.Probes[1].Measurement.Available);
        Assert.Equal(0, result.Probes[1].EncodedBytes);
        Assert.True(result.Decision.FellBack);
        Assert.Contains("scratch disk unavailable", result.Decision.Reason);
    }

    [Fact]
    public async Task An_encoder_failure_records_zero_bytes_and_preserves_its_actual_reason()
    {
        var result = await PerceptualSearchRunner.RunAsync(PerceptualQualityPlan.Image("libwebp", 80),
            (value, index, token) => Task.FromResult(new PerceptualSearchProbe(value, 0,
                PerceptualSearchMeasurement.Unavailable("encoder failed before producing a sample"))), CancellationToken.None);
        Assert.True(result.Decision.FellBack);
        Assert.Equal(0, result.Probes.Single().EncodedBytes);
        Assert.Contains("encoder failed", result.Decision.Reason);
    }

    private static ImagePerceptualReport Report(double score) => new(new(32, 32, false, 0, score, [score]), null,
        Source, Candidate, Source, 1);

    private static AudioQualityReport Audio(double rightDistance) => new("Server", RemoteAudioQualityEvidence.From(new(
        true, null, new(30, 2, 48000, "stereo"), new(30, 2, 48000, "stereo"), Source, Candidate,
        Source, Source, Source, [new(new(0, 30), new(1440000, [0.001, rightDistance]))], 1)), null);
}
