using System.Text.Json;
using Optimisarr.Api.Workers;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Tests;

/// <summary>
/// Whether a worker's measurement may stand in for this machine's. The dangerous shapes are all
/// about binding, not numbers: evidence for other bytes, or under another policy, or none at all.
/// </summary>
public sealed class DeliveredQualityEvidenceTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly VerificationPolicy Policy = VerificationPolicy.Default with { QualityGateEnabled = true };

    private static JobLease Lease(
        string? contractModel = "vmaf_v0.6.1",
        double contractHarmonic = 93,
        double contractMinimum = 80,
        QualityScores? scores = null,
        string? measuredSource = "src",
        string? measuredCandidate = "cand",
        string? delivered = "cand") => new()
    {
        Id = Guid.NewGuid(),
        JobId = 1,
        WorkerId = 1,
        QualityContractJson = contractModel is null
            ? null
            : JsonSerializer.Serialize(new RemoteQualityContract(contractModel, "Full file", contractHarmonic, contractMinimum, [["-i", "{{distorted}}"]]), Json),
        QualityScoresJson = scores is null ? null : JsonSerializer.Serialize(scores, Json),
        QualitySourceSha256 = measuredSource,
        QualityCandidateSha256 = measuredCandidate,
        DeliveredSha256 = delivered,
    };

    private static readonly QualityScores Good = new(95, 94.9, 90, null, null, VmafFifthPercentile: 92, FrameCount: 100);

    [Fact]
    public void Bound_evidence_is_handed_to_verification_with_its_sampling()
    {
        var resolved = DeliveredQualityEvidence.Resolve(Lease(scores: Good), "src", Policy);

        Assert.True(resolved.WasAsked);
        Assert.NotNull(resolved.Accepted);
        Assert.Equal(94.9, resolved.Accepted!.Result.Scores!.VmafHarmonicMean);
        Assert.Equal("Full file", resolved.Accepted.Sampling);
        Assert.Empty(resolved.Objections);
    }

    [Fact]
    public void A_lease_that_asked_for_nothing_hands_nothing_on_and_is_not_a_problem()
    {
        var resolved = DeliveredQualityEvidence.Resolve(Lease(contractModel: null), "src", Policy);

        Assert.False(resolved.WasAsked);
        Assert.Null(resolved.Accepted);
    }

    [Fact]
    public void Evidence_that_never_arrived_is_an_objection_not_a_pass()
    {
        var resolved = DeliveredQualityEvidence.Resolve(Lease(scores: null), "src", Policy);

        Assert.True(resolved.WasAsked);
        Assert.Null(resolved.Accepted);
        Assert.Contains(resolved.Objections, reason => reason.Contains("no quality evidence"));
    }

    [Fact]
    public void Evidence_for_a_candidate_other_than_the_one_delivered_is_refused()
    {
        // Encode twice, measure the good one, deliver the other: the shape the candidate hash exists for.
        var resolved = DeliveredQualityEvidence.Resolve(Lease(scores: Good, measuredCandidate: "other"), "src", Policy);

        Assert.Null(resolved.Accepted);
        Assert.Contains(resolved.Objections, reason => reason.Contains("different candidate"));
    }

    [Fact]
    public void Evidence_measured_under_an_easier_policy_than_now_required_is_refused()
    {
        var lease = Lease(scores: Good, contractHarmonic: 80, contractMinimum: 60);
        var stricter = Policy with { MinimumVmafHarmonicMean = 93, MinimumVmafMin = 80 };

        var resolved = DeliveredQualityEvidence.Resolve(lease, "src", stricter);

        Assert.Null(resolved.Accepted);
        Assert.Contains(resolved.Objections, reason => reason.Contains("weaker policy"));
    }

    [Fact]
    public void Low_scores_are_still_handed_on_so_the_ordinary_gate_fails_them()
    {
        // A number below the floor is not a binding problem; it is the answer. Verification
        // reports it and fails the candidate with the same wording a local measurement would get.
        var poor = Good with { VmafHarmonicMean = 70, VmafFifthPercentile = 50 };

        var resolved = DeliveredQualityEvidence.Resolve(Lease(scores: poor), "src", Policy);

        Assert.NotNull(resolved.Accepted);
        Assert.Equal(70, resolved.Accepted!.Result.Scores!.VmafHarmonicMean);
    }
}
