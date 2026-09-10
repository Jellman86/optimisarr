using System.Text.Json;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Api.Workers;

/// <summary>What verification is handed for a delivered candidate: scores to use, or why not.</summary>
internal sealed record DeliveredQuality(
    RemoteQuality? Accepted,
    IReadOnlyList<string> Objections,
    bool WasAsked);

/// <summary>
/// Turns what a lease recorded — the contract sent, the logs the worker returned — into a
/// measurement verification may use in place of its own, or the reasons it may not. Every
/// refusal comes from <see cref="RemoteQualityEvidenceValidator"/>; this only rebuilds its inputs
/// from the lease and the delivered candidate's real hash.
/// </summary>
internal static class DeliveredQualityEvidence
{
    public static DeliveredQuality Resolve(JobLease lease, string? sourceSha256, VerificationPolicy policy)
    {
        var contract = lease.QualityContractJson is { } contractJson
            ? JsonSerializer.Deserialize<RemoteQualityContract>(contractJson, Json.Options)
            : null;
        if (contract is null)
        {
            return new DeliveredQuality(null, [], WasAsked: false);
        }

        var scores = lease.QualityScoresJson is { } scoresJson
            ? JsonSerializer.Deserialize<QualityScores>(scoresJson, Json.Options)
            : null;
        var evidence = scores is null
            ? null
            : new RemoteQualityEvidence(
                lease.QualitySourceSha256,
                lease.QualityCandidateSha256,
                scores.VmafHarmonicMean ?? 0,
                scores.VmafFifthPercentile ?? scores.VmafMin ?? 0,
                contract.Model,
                contract.MinimumHarmonicMean,
                contract.MinimumMinimum);

        var required = new VmafRequirement(
            sourceSha256 ?? string.Empty,
            lease.DeliveredSha256 ?? string.Empty,
            policy.MinimumVmafHarmonicMean,
            policy.MinimumVmafMin);
        var verdict = RemoteQualityEvidenceValidator.Validate(evidence, required);

        // The validator's numeric objections are the library's own gate speaking early. They are
        // still handed on as scores, so the report shows the measured numbers and the gate fails
        // the candidate the ordinary way, with the ordinary wording.
        var bindingObjections = verdict.Reasons
            .Where(reason => !reason.StartsWith("The harmonic mean", StringComparison.Ordinal)
                && !reason.StartsWith("The lowest measured", StringComparison.Ordinal))
            .ToList();
        if (scores is null || bindingObjections.Count > 0)
        {
            return new DeliveredQuality(null, bindingObjections, WasAsked: true);
        }

        var result = QualityResult.Ok(scores with { ModelVersion = scores.ModelVersion ?? contract.Model });
        return new DeliveredQuality(new RemoteQuality(result, contract.Sampling), [], WasAsked: true);
    }

    internal static class Json
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    }
}
