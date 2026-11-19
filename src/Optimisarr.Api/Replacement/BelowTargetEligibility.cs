using System.Text.Json;
using System.Text.Json.Serialization;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Verification;
using Optimisarr.Data;

namespace Optimisarr.Api.Replacement;

/// <summary>
/// Reads a stored job into <see cref="BelowTargetAcceptance"/>, so the Queue's button and the
/// replacement that acts on it apply one rule to the same facts.
/// </summary>
public static class BelowTargetEligibility
{
    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static BelowTargetAcceptanceDecision Decide(Job job, MediaKind kind, bool savesToAnotherFolder, bool dryRun) =>
        BelowTargetAcceptance.Evaluate(ReadReport(job.VerificationReportJson), new BelowTargetAcceptanceFacts(
            IsNormalJob: job.Type == JobType.Normal,
            IsFailed: job.Status == JobStatus.Failed,
            Kind: kind,
            SuperResolutionApplied: job.SuperResolutionApplied,
            SavesToAnotherFolder: savesToAnotherFolder,
            DryRun: dryRun,
            HasVerifiedIdentity: FileContentIdentity.IsHash(job.VerifiedSourceSha256)
                && FileContentIdentity.IsHash(job.VerifiedOutputSha256)
                && !string.IsNullOrEmpty(job.WorkOutputPath)));

    public static string Explain(BelowTargetRefusal refusal) => refusal switch
    {
        BelowTargetRefusal.NotFailed => "Only a failed job can be accepted below its target.",
        BelowTargetRefusal.NotAnOptimisation => "Previews and calibration samples cannot replace an original.",
        BelowTargetRefusal.NotVideo => "Only a video's picture-quality score can be accepted below target.",
        BelowTargetRefusal.SuperResolution => "Super-resolution output is judged differently and cannot be accepted below target.",
        BelowTargetRefusal.SavesToAnotherFolder => "This library saves optimised files to another folder instead of replacing originals.",
        BelowTargetRefusal.DryRun => "Dry-run mode is on, so originals are left untouched.",
        BelowTargetRefusal.NoVerifiedIdentity => "There is no record of the exact files that were measured, or the output is gone. Try the file again.",
        BelowTargetRefusal.BelowCatastrophicFloor => "Part of the picture scored below the safety floor, which usually means a damaged decode. This cannot be accepted.",
        BelowTargetRefusal.QualityNotMeasured => "The picture quality could not be measured, so there is no score to accept.",
        _ => "Only a job whose sole failed check is the picture-quality score can be accepted below target."
    };

    private static VerificationReport? ReadReport(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<VerificationReport>(json, ReportJson); }
        // An old or partly written report is simply not evidence for an acceptance.
        catch (JsonException) { return null; }
    }
}
