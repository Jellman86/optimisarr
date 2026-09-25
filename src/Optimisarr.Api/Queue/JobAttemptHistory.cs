using System.Text.Json;
using Optimisarr.Data;

namespace Optimisarr.Api.Queue;

/// <summary>A completed candidate superseded by a retry. The current candidate remains on Job.</summary>
public sealed record JobAttemptSnapshot(
    int Number,
    string? WorkerName,
    string? VideoEncoder,
    string? HardwareDecoder,
    DateTimeOffset? StartedAt,
    DateTimeOffset EndedAt,
    bool? VerificationPassed,
    string? VerificationReportJson,
    DateTimeOffset? VerifiedAt,
    long? OutputSizeBytes,
    string Outcome,
    string Reason,
    string? FfmpegArguments,
    string? ProcessLog);

internal static class JobAttemptHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal const string SoftwareDecodeReason = "SoftwareDecode";

    internal static IReadOnlyList<JobAttemptSnapshot> Read(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<JobAttemptSnapshot>>(json, JsonOptions) ?? [];

    /// <summary>
    /// Archive the rejected candidate and clear all active-attempt facts in the same tracked Job.
    /// The caller saves once, so an API reader cannot see a queued retry with the prior verdict.
    /// </summary>
    internal static void RequeueAfterRejectedCandidate(
        Job job, string workerName, string? hardwareDecoder, DateTimeOffset nowUtc)
    {
        var history = Read(job.AttemptHistoryJson).ToList();
        job.ExecutionAttempt = Math.Max(job.ExecutionAttempt, history.Count + 1);
        history.Add(new JobAttemptSnapshot(
            job.ExecutionAttempt,
            workerName,
            job.VideoEncoder,
            hardwareDecoder,
            job.StartedAt,
            nowUtc,
            job.VerificationPassed,
            job.VerificationReportJson,
            job.VerifiedAt,
            job.OutputSizeBytes,
            "Rejected",
            "HardwareDecodeCorruption",
            job.FfmpegArguments,
            job.ProcessLog));
        job.AttemptHistoryJson = JsonSerializer.Serialize(history, JsonOptions);
        job.RetryReason = SoftwareDecodeReason;
        job.PreferSoftwareDecode = true;
        job.Status = JobStatus.Queued;
        job.Progress = 0;
        job.ErrorMessage = null;
        job.FailureCategory = null;
        job.ProcessLog = null;
        job.WorkOutputPath = null;
        job.FfmpegArguments = null;
        job.VideoEncoder = null;
        job.RequestedVideoQuality = null;
        job.EffectiveVideoQuality = null;
        job.VideoQualityMode = null;
        job.OutputSizeBytes = null;
        job.VerificationPassed = null;
        job.VerificationReportJson = null;
        job.VerifiedAt = null;
        job.StartedAt = null;
        job.FinishedAt = null;
        job.UpdatedAt = nowUtc;
    }
}
