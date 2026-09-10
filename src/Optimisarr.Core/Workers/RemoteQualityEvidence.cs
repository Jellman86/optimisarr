namespace Optimisarr.Core.Workers;

/// <summary>
/// A perceptual quality measurement taken by a remote worker on the candidate it produced.
///
/// This is the one thing Optimisarr accepts from a sidecar without deriving it again. Everything
/// else about a returned candidate — that it decodes, that its duration and tail are intact, that
/// its streams and size are right — is re-checked locally, because those checks are cheap relative
/// to the encode. VMAF is not: it is roughly half the cost of verification, so re-measuring it here
/// would give back most of the benefit of having distributed the work at all.
///
/// Accepting it is only safe because the measurement is bound to the exact bytes it was taken from
/// and the exact thresholds it was taken under. A score that cannot be tied to all three is not
/// weak evidence — it is evidence about something else.
/// </summary>
public sealed record RemoteQualityEvidence(
    string? SourceSha256,
    string? CandidateSha256,
    double HarmonicMean,
    double Minimum,

    /// <summary>
    /// The VMAF model used. Without it the number has no defined meaning: the same file scores
    /// differently under the HD and 4K models, so an unlabelled score cannot be compared to a
    /// threshold at all.
    /// </summary>
    string? Model,

    /// <summary>The harmonic-mean floor the worker says it was grading against.</summary>
    double MeasuredAgainstHarmonicMean,

    /// <summary>The per-frame floor the worker says it was grading against.</summary>
    double MeasuredAgainstMinimum);

/// <summary>What the control plane actually asked for, and of which bytes.</summary>
public sealed record VmafRequirement(
    string SourceSha256,
    string CandidateSha256,
    double MinimumHarmonicMean,
    double MinimumMinimum);

/// <summary>The verdict, with every objection named rather than only the first.</summary>
public sealed record EvidenceVerdict(bool Accepted, IReadOnlyList<string> Reasons);

/// <summary>
/// Decides whether a remote quality measurement may be believed.
///
/// Fails closed throughout. The failure this guards against is not a worker that lies loudly — it
/// is one that answers a slightly different question: measuring a different encode, or grading
/// itself against an easier policy, and returning a number that looks like an answer.
/// </summary>
public static class RemoteQualityEvidenceValidator
{
    public static EvidenceVerdict Validate(RemoteQualityEvidence? evidence, VmafRequirement required)
    {
        if (evidence is null)
        {
            // Absent evidence must never read as "nothing objected", or a worker could skip
            // measuring entirely and have its candidate sail through unexamined.
            return new EvidenceVerdict(false, ["The worker returned no quality evidence for this candidate."]);
        }

        var reasons = new List<string>();

        if (!Matches(evidence.SourceSha256, required.SourceSha256))
        {
            reasons.Add("The quality evidence was measured against a different source.");
        }

        if (!Matches(evidence.CandidateSha256, required.CandidateSha256))
        {
            // The dangerous shape: encode twice, measure the good one, deliver the other.
            reasons.Add("The quality evidence was measured against a different candidate than the one delivered.");
        }

        if (string.IsNullOrWhiteSpace(evidence.Model))
        {
            reasons.Add("The quality evidence names no VMAF model, so its scores cannot be compared to a threshold.");
        }

        // Grading against an easier policy is not answering the question that was asked, however
        // genuine the number. Stricter is fine — passing a harder test than the one set still
        // passes the one set.
        if (evidence.MeasuredAgainstHarmonicMean < required.MinimumHarmonicMean
            || evidence.MeasuredAgainstMinimum < required.MinimumMinimum)
        {
            reasons.Add(
                "The quality evidence was measured against a weaker policy than this library requires " +
                $"({evidence.MeasuredAgainstHarmonicMean}/{evidence.MeasuredAgainstMinimum} " +
                $"against {required.MinimumHarmonicMean}/{required.MinimumMinimum}).");
        }

        if (evidence.HarmonicMean < required.MinimumHarmonicMean)
        {
            reasons.Add(
                $"The harmonic mean VMAF of {evidence.HarmonicMean} is below the required " +
                $"{required.MinimumHarmonicMean}.");
        }

        if (evidence.Minimum < required.MinimumMinimum)
        {
            // Why a floor exists alongside a mean: an average hides a few seconds of ruined
            // picture, and an average is exactly what a poor encode would rather be judged on.
            reasons.Add(
                $"The lowest measured VMAF of {evidence.Minimum} is below the required " +
                $"{required.MinimumMinimum}.");
        }

        return new EvidenceVerdict(reasons.Count == 0, reasons);
    }

    private static bool Matches(string? supplied, string expected) =>
        !string.IsNullOrWhiteSpace(supplied)
        && string.Equals(supplied, expected, StringComparison.OrdinalIgnoreCase);
}
