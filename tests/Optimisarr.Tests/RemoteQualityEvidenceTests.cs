using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

/// <summary>
/// A quality score measured on someone else's machine is the one thing Optimisarr accepts without
/// re-deriving it, so what makes a score admissible is worth pinning precisely. Everything else a
/// returned candidate claims is re-checked locally; this is the exception, and it is only safe
/// because the evidence is bound to the exact bytes and the exact policy it was measured under.
///
/// Every case here fails closed. A score that cannot be tied to this source, this candidate, and
/// the thresholds actually asked for is not weak evidence — it is evidence about something else.
/// </summary>
public class RemoteQualityEvidenceTests
{
    private const string SourceHash = "aaaa1111";
    private const string CandidateHash = "bbbb2222";

    private static RemoteQualityEvidence Good() => new(
        SourceSha256: SourceHash,
        CandidateSha256: CandidateHash,
        HarmonicMean: 96.2,
        Minimum: 88.0,
        Model: "vmaf_v0.6.1",
        MeasuredAgainstHarmonicMean: 93.0,
        MeasuredAgainstMinimum: 80.0);

    private static VmafRequirement Required() => new(
        SourceSha256: SourceHash,
        CandidateSha256: CandidateHash,
        MinimumHarmonicMean: 93.0,
        MinimumMinimum: 80.0);

    [Fact]
    public void Evidence_for_this_source_and_candidate_at_the_requested_policy_is_accepted()
    {
        var verdict = RemoteQualityEvidenceValidator.Validate(Good(), Required());

        Assert.True(verdict.Accepted);
        Assert.Empty(verdict.Reasons);
    }

    [Fact]
    public void Missing_evidence_is_refused_rather_than_treated_as_a_pass()
    {
        // Fail closed. Absent evidence must never read as "nothing objected", or a worker could
        // skip measuring entirely and have its candidate sail through.
        var verdict = RemoteQualityEvidenceValidator.Validate(null, Required());

        Assert.False(verdict.Accepted);
        Assert.Contains(verdict.Reasons, r => r.Contains("no quality evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evidence_measured_against_a_different_source_is_refused()
    {
        // The score may be perfectly real and still say nothing about this job.
        var verdict = RemoteQualityEvidenceValidator.Validate(
            Good() with { SourceSha256 = "different" }, Required());

        Assert.False(verdict.Accepted);
        Assert.Contains(verdict.Reasons, r => r.Contains("source", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evidence_measured_against_a_different_candidate_is_refused()
    {
        // The dangerous shape: encode twice, measure the good one, deliver the bad one.
        var verdict = RemoteQualityEvidenceValidator.Validate(
            Good() with { CandidateSha256 = "another file" }, Required());

        Assert.False(verdict.Accepted);
        Assert.Contains(verdict.Reasons, r => r.Contains("candidate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evidence_measured_against_weaker_thresholds_than_were_asked_for_is_refused()
    {
        // A worker that graded itself against an easier policy has not answered the question the
        // control plane asked, even if its score is genuine and high.
        var verdict = RemoteQualityEvidenceValidator.Validate(
            Good() with { MeasuredAgainstHarmonicMean = 80.0 }, Required());

        Assert.False(verdict.Accepted);
        Assert.Contains(verdict.Reasons, r => r.Contains("polic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evidence_measured_against_stricter_thresholds_is_accepted()
    {
        // Passing a harder test than the one set is still passing the one set.
        var verdict = RemoteQualityEvidenceValidator.Validate(
            Good() with { MeasuredAgainstHarmonicMean = 96.0, MeasuredAgainstMinimum = 90.0 },
            Required());

        Assert.True(verdict.Accepted);
    }

    [Fact]
    public void A_score_below_the_required_harmonic_mean_is_refused()
    {
        var verdict = RemoteQualityEvidenceValidator.Validate(
            Good() with { HarmonicMean = 91.0 }, Required());

        Assert.False(verdict.Accepted);
        Assert.Contains(verdict.Reasons, r => r.Contains("harmonic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_single_catastrophic_frame_is_refused_even_when_the_mean_is_excellent()
    {
        // The reason a minimum exists at all: an average hides a few seconds of ruined picture, and
        // an average is exactly what a worker would prefer to be judged on.
        var verdict = RemoteQualityEvidenceValidator.Validate(
            Good() with { HarmonicMean = 99.0, Minimum = 12.0 }, Required());

        Assert.False(verdict.Accepted);
        Assert.Contains(verdict.Reasons, r => r.Contains("lowest", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Evidence_naming_no_model_is_refused(string? model)
    {
        // Without the model, the number has no defined meaning — the same file scores differently
        // under the HD and 4K models, so an unlabelled score is not comparable to a threshold.
        var verdict = RemoteQualityEvidenceValidator.Validate(Good() with { Model = model }, Required());

        Assert.False(verdict.Accepted);
        Assert.Contains(verdict.Reasons, r => r.Contains("model", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_problem_is_reported_rather_than_only_the_first()
    {
        var verdict = RemoteQualityEvidenceValidator.Validate(
            Good() with { SourceSha256 = "x", CandidateSha256 = "y", Model = null }, Required());

        Assert.False(verdict.Accepted);
        Assert.True(verdict.Reasons.Count >= 3, $"expected several reasons, got {verdict.Reasons.Count}");
    }
}
