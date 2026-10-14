using System.Text.Json;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

public sealed class RemotePerceptualSearchStateTests
{
    private static readonly string Source = new('a', 64), Candidate = new('b', 64), Tool = new('c', 64);
    private static RemotePerceptualSearchContract Contract() => new(Guid.NewGuid(), MediaKind.Image, "libwebp",
        PerceptualQualityPlan.Image("libwebp", 80), VerificationPolicy.Default with { ImagePerceptualGateEnabled = true,
            MinimumImagePerceptualScore = 85, ImageQualityGateEnabled = false }, [], SourceImage: new(32, 32, false));
    private static RemoteImagePerceptualEvidence Image(double score, string hash) => RemoteImagePerceptualEvidence.From(
        new(new(32, 32, false, 0, score, [score]), null, Source, hash, Tool, 1, MeasurementLocation: "Worker"));
    private static RemotePerceptualProbeReport Report(RemotePerceptualSearchState state, int? value = null) =>
        new(state.Contract.Id, state.Contract.Encoder, value ?? state.AskedValue!.Value, Source, new(Image: Image(100, Source), ImageMetadata: ImageMetadataResult.Ok(new(false, false))),
            [new(0, 1000 + (value ?? state.AskedValue!.Value) * 10, Source, Candidate,
                Image: Image((value ?? state.AskedValue!.Value) >= 60 ? 90 : 80, Candidate))], EncoderSha256: Tool);

    [Fact]
    public void A_changed_encoder_binary_cannot_reuse_the_previous_search_even_with_the_same_native_score()
    {
        var state = RemotePerceptualSearchState.Start(Contract());
        state = RemotePerceptualSearchCoordinator.Advance(state, Source, Report(state), new('d', 64)).State!;
        Assert.False(RemotePerceptualSearchCoordinator.Advance(state, Source, Report(state) with { EncoderSha256 = Candidate }, new('e', 64)).Accepted);
    }

    [Fact]
    public void Original_metadata_evidence_cannot_change_between_settings_for_the_same_original_hash()
    {
        var state = RemotePerceptualSearchState.Start(Contract());
        state = RemotePerceptualSearchCoordinator.Advance(state, Source, Report(state), new('d', 64)).State!;
        var report = Report(state);
        var changed = report with { Source = report.Source with { ImageMetadata = ImageMetadataResult.Ok(new(true, false)) } };
        var result = RemotePerceptualSearchCoordinator.Advance(state, Source, changed, new('e', 64));
        Assert.False(result.Accepted); Assert.Single(state.Probes);
    }

    [Fact]
    public void A_forged_settled_decision_cannot_authorise_an_encode_before_the_search_has_finished()
    {
        var state = RemotePerceptualSearchState.Start(Contract());
        state = RemotePerceptualSearchCoordinator.Advance(state, Source, Report(state), new('d', 64)).State!;
        Assert.False((state with { AskedValue = null, Decision = new(null, 80, false, "settled") }).CanEncode);
    }

    [Fact]
    public void Search_state_round_trips_without_losing_the_plan_or_native_tool_identity()
    {
        var state = RemotePerceptualSearchState.Start(Contract());
        state = RemotePerceptualSearchCoordinator.Advance(state, Source, Report(state), new('d', 64)).State!;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var restored = JsonSerializer.Deserialize<RemotePerceptualSearchState>(JsonSerializer.Serialize(state, options), options)!;
        var outcome = RemotePerceptualSearchCoordinator.Advance(restored, Source, Report(restored), new('e', 64));
        Assert.True(outcome.Accepted, outcome.Error);
        Assert.Equal(2, outcome.State!.Probes.Count);
    }

    [Fact]
    public void A_different_native_tool_cannot_adopt_samples_measured_by_the_previous_binary()
    {
        var state = RemotePerceptualSearchState.Start(Contract());
        state = RemotePerceptualSearchCoordinator.Advance(state, Source, Report(state), new('d', 64)).State!;
        var report = Report(state);
        report = report with { Source = report.Source with { Image = report.Source.Image! with { Assessment = report.Source.Image.Assessment with { MetricSha256 = Candidate } } },
            Samples = [report.Samples[0] with { Image = report.Samples[0].Image! with
                { Assessment = report.Samples[0].Image!.Assessment with { MetricSha256 = Candidate } } }] };
        Assert.False(RemotePerceptualSearchCoordinator.Advance(state, Source, report, new('e', 64)).Accepted);
        Assert.Single(state.Probes);
    }

    [Fact]
    public void Corrupt_stored_state_never_allows_an_encode_or_throws_while_being_refused()
    {
        var state = RemotePerceptualSearchState.Start(Contract());
        var corrupt = state with { Probes = null!, Decision = new(null, 80, true, "corrupt"), AskedValue = null };
        Assert.False(corrupt.CanEncode);
        Assert.False(RemotePerceptualSearchCoordinator.Advance(corrupt, Source, Report(state), new('d', 64)).Accepted);
    }

    [Fact]
    public void A_search_cannot_encode_until_the_server_settles_within_its_four_candidate_budget()
    {
        var state = RemotePerceptualSearchState.Start(Contract());
        Assert.False(state.CanEncode);
        for (var index = 0; index < 4; index++)
        {
            var advanced = RemotePerceptualSearchCoordinator.Advance(state, Source, Report(state), new string((char)('1' + index), 64));
            Assert.True(advanced.Accepted);
            state = advanced.State!;
        }
        Assert.True(state.CanEncode);
        Assert.Null(state.AskedValue);
        Assert.Equal(4, state.Probes.Count);
        Assert.Equal(62, state.Decision!.SelectedValue);
        Assert.False(state.Decision.FellBack);
        Assert.False(RemotePerceptualSearchCoordinator.Advance(state, Source, Report(state, 50), new('f', 64)).Accepted);
    }

    [Fact]
    public void A_retried_identical_report_replays_the_decision_without_spending_another_probe()
    {
        var initial = RemotePerceptualSearchState.Start(Contract());
        var report = Report(initial);
        var first = RemotePerceptualSearchCoordinator.Advance(initial, Source, report, new('d', 64));
        var replay = RemotePerceptualSearchCoordinator.Advance(first.State!, Source, report, new('d', 64));
        Assert.True(replay.Accepted);
        Assert.True(replay.Duplicate);
        Assert.Same(first.State, replay.State);
        Assert.Single(replay.State!.Probes);
        Assert.Equal(first.State!.Decision, replay.State.Decision);
        var conflicting = RemotePerceptualSearchCoordinator.Advance(first.State!, Source, report, new('e', 64));
        Assert.False(conflicting.Accepted);
        Assert.Null(conflicting.State);
    }

    [Fact]
    public void A_source_refusal_is_terminal_without_permitting_a_baseline_encode()
    {
        var initial = RemotePerceptualSearchState.Start(Contract());
        var rejected = Report(initial) with { Source = new(Error: "Unsupported source profile"), Samples = [] };
        var outcome = RemotePerceptualSearchCoordinator.Advance(initial, Source, rejected, new('d', 64));
        Assert.True(outcome.Accepted);
        Assert.True(outcome.State!.SourceRejected);
        Assert.False(outcome.State.CanEncode);
        Assert.Null(outcome.State.AskedValue);
        Assert.Empty(outcome.State.Probes);
        Assert.Contains("Unsupported source", outcome.State.SourceError);
    }

    [Fact]
    public void An_explicit_sample_failure_settles_at_the_saved_setting_with_retained_reason()
    {
        var initial = RemotePerceptualSearchState.Start(Contract());
        var failed = Report(initial) with { Error = "Native scorer timed out", Samples = [] };
        var result = RemotePerceptualSearchCoordinator.Advance(initial, Source, failed, new('d', 64));
        Assert.True(result.Accepted);
        Assert.True(result.State!.CanEncode);
        Assert.True(result.State.Decision!.FellBack);
        Assert.Equal(80, result.State.Decision.SelectedValue);
        Assert.Contains("timed out", result.State.Decision.Reason);
        Assert.Single(result.State.Probes);
    }

    [Fact]
    public void Old_contract_source_or_out_of_order_reports_cannot_mutate_current_search_state()
    {
        var state = RemotePerceptualSearchState.Start(Contract());
        var old = Report(state);
        state = RemotePerceptualSearchCoordinator.Advance(state, Source, old, new('d', 64)).State!;
        foreach (var changed in new[] { old with { ContractId = Guid.NewGuid() }, old with { SourceSha256 = Candidate }, Report(state, 56) })
            Assert.False(RemotePerceptualSearchCoordinator.Advance(state, Source, changed, new('e', 64)).Accepted);
        Assert.Single(state.Probes);
        Assert.False(state.CanEncode);
    }

    [Fact]
    public void A_changed_source_cannot_adopt_prior_probes_even_when_its_new_profile_is_identical()
    {
        var state = RemotePerceptualSearchState.Start(Contract());
        state = RemotePerceptualSearchCoordinator.Advance(state, Source, Report(state), new('d', 64)).State!;
        var changedHash = new string('f', 64);
        var report = Report(state);
        report = report with { SourceSha256 = changedHash,
            Source = report.Source with { Image = report.Source.Image! with { Assessment = report.Source.Image.Assessment with
                { SourceSha256 = changedHash, CandidateSha256 = changedHash } } },
            Samples = [report.Samples[0] with { ReferenceSha256 = changedHash,
                Image = report.Samples[0].Image! with { Assessment = report.Samples[0].Image!.Assessment with { SourceSha256 = changedHash } } }] };
        Assert.False(RemotePerceptualSearchCoordinator.Advance(state, changedHash, report, new('e', 64)).Accepted);
        Assert.Single(state.Probes);
    }
}
