using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Data;

namespace Optimisarr.Api.Diagnostics;

internal sealed record DiagnosticBundleManifest(
    int SchemaVersion,
    Guid SessionId,
    DateTimeOffset CollectedAt,
    bool PathsIncluded,
    bool EventLimitReached,
    IReadOnlyList<string> Omissions);

internal sealed record DiagnosticReportSummary(
    bool Passed,
    IReadOnlyList<DiagnosticCheckSummary> Checks,
    DiagnosticVmafScores? VmafScores,
    string? Encoder,
    string? VerificationLocation);

internal sealed record DiagnosticVmafScores(
    double? Mean,
    double? HarmonicMean,
    double? FifthPercentile,
    double? LowestFrame,
    int? FrameCount);

internal sealed record DiagnosticCheckSummary(string Name, string Outcome);

internal sealed record DiagnosticJobSummary(
    int Id,
    int? LibraryId,
    string? Path,
    string Status,
    int Attempt,
    string? VideoEncoder,
    string? SourceSha256,
    long? OutputSizeBytes,
    bool? VerificationPassed,
    string? FailureCategory,
    DiagnosticReportSummary? CurrentReport);

internal sealed record DiagnosticAttemptSummary(
    int Number,
    int? WorkerId,
    string? Encoder,
    string? HardwareDecoder,
    DateTimeOffset? StartedAt,
    DateTimeOffset EndedAt,
    string Outcome,
    string ReasonCode,
    bool? VerificationPassed,
    DiagnosticReportSummary? Report);

internal sealed record DiagnosticLeaseSummary(
    Guid Id,
    int WorkerId,
    string? SidecarVersion,
    string OperatingSystem,
    int ProtocolVersion,
    DateTimeOffset AcquiredAt,
    DateTimeOffset? EndedAt,
    string State,
    string? Stage,
    string? DeliveredSha256,
    bool VerificationEvidencePresent);

internal sealed record DiagnosticEventSummary(
    long Id,
    DateTimeOffset OccurredAt,
    int JobId,
    int Attempt,
    Guid? LeaseId,
    int? WorkerId,
    string ReasonCode,
    string PreviousStatus,
    string CurrentStatus);

internal sealed record DiagnosticJobBundle(
    DiagnosticBundleManifest Manifest,
    DiagnosticJobSummary Job,
    IReadOnlyList<DiagnosticAttemptSummary> Attempts,
    IReadOnlyList<DiagnosticLeaseSummary> Leases,
    IReadOnlyList<DiagnosticEventSummary> Events);

internal static class DiagnosticJobBundleQueries
{
    private const int MaximumAttempts = 200;
    private const int MaximumLeases = 500;
    private const int MaximumChecksPerReport = 100;
    private const int MaximumStoredReportCharacters = 1_000_000;
    private const int MaximumStoredAttemptCharacters = 2_000_000;

    private static readonly JsonSerializerOptions ReportOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<DiagnosticJobBundle> BuildAsync(
        OptimisarrDbContext db,
        Guid sessionId,
        int jobId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var session = await db.DiagnosticCaptureSessions.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == sessionId, cancellationToken)
            ?? throw new KeyNotFoundException("Diagnostic session not found.");
        if (session.ScopedJobId is { } scoped && scoped != jobId)
        {
            throw new KeyNotFoundException("This job is outside the diagnostic session.");
        }

        var job = await db.Jobs.AsNoTracking().Include(candidate => candidate.MediaFile)
            .FirstOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken)
            ?? throw new KeyNotFoundException("Job not found.");
        var allLeases = await db.JobLeases.AsNoTracking().Include(lease => lease.Worker)
            .Where(lease => lease.JobId == jobId)
            .ToListAsync(cancellationToken);
        var leases = allLeases.OrderBy(lease => lease.AcquiredAt).TakeLast(MaximumLeases).ToList();
        var events = await db.DiagnosticEvents.AsNoTracking()
            .Where(entry => entry.SessionId == sessionId && entry.JobId == jobId)
            .OrderBy(entry => entry.Id)
            .Take(Optimisarr.Core.Diagnostics.DiagnosticCapturePolicy.MaximumEvents)
            .ToListAsync(cancellationToken);

        var omissions = new List<string>
        {
            "Sidecar-local diagnostic logs are not collected by this bundle; server-held lease and evidence-presence records are included.",
            "Raw FFmpeg output and commands are omitted because they may contain paths or credentials.",
            "Only retries already archived in job attempt history are listed as historical attempts; the current attempt is summarised on the job.",
            "Each verification report includes at most 100 check names and outcomes; raw check details are omitted."
        };
        if (allLeases.Count > MaximumLeases)
        {
            omissions.Add("Older worker leases were omitted to bound bundle size.");
        }
        IReadOnlyList<JobAttemptSnapshot> attempts;
        try
        {
            if (job.AttemptHistoryJson?.Length > MaximumStoredAttemptCharacters)
            {
                attempts = [];
                omissions.Add("Stored attempt history exceeded the bundle size limit.");
            }
            else
            {
                var storedAttempts = JobAttemptHistory.Read(job.AttemptHistoryJson);
                attempts = storedAttempts.TakeLast(MaximumAttempts).ToList();
                if (storedAttempts.Count > MaximumAttempts)
                {
                    omissions.Add("Older attempts were omitted to bound bundle size.");
                }
            }
        }
        catch (JsonException)
        {
            attempts = [];
            omissions.Add("Stored attempt history could not be parsed.");
        }

        var summary = new DiagnosticJobSummary(
            job.Id,
            job.LibraryId,
            session.IncludePaths ? job.MediaFile?.Path : null,
            job.Status.ToString(),
            job.ExecutionAttempt,
            DiagnosticSafeFields.Encoder(job.VideoEncoder),
            DiagnosticSafeFields.Sha256(job.SourceSha256),
            job.OutputSizeBytes,
            job.VerificationPassed,
            job.FailureCategory?.ToString(),
            SummariseReport(job.VerificationReportJson));
        return new DiagnosticJobBundle(
            new DiagnosticBundleManifest(1, session.Id, nowUtc, session.IncludePaths,
                session.EventLimitReached, omissions),
            summary,
            attempts.Select(attempt => new DiagnosticAttemptSummary(
                attempt.Number,
                ResolveAttemptWorkerId(attempt, leases),
                DiagnosticSafeFields.Encoder(attempt.VideoEncoder),
                DiagnosticSafeFields.Decoder(attempt.HardwareDecoder),
                attempt.StartedAt,
                attempt.EndedAt,
                DiagnosticSafeFields.AttemptOutcome(attempt.Outcome),
                DiagnosticSafeFields.AttemptReason(attempt.Reason),
                attempt.VerificationPassed,
                SummariseReport(attempt.VerificationReportJson))).ToList(),
            leases.OrderBy(lease => lease.AcquiredAt).Select(lease => new DiagnosticLeaseSummary(
                lease.Id,
                lease.WorkerId,
                DiagnosticSafeFields.Version(lease.Worker?.SidecarVersion),
                DiagnosticSafeFields.OperatingSystem(lease.Worker?.OperatingSystem),
                lease.Worker?.ProtocolVersion ?? 0,
                lease.AcquiredAt,
                lease.EndedAt,
                lease.State.ToString(),
                lease.Stage?.ToString(),
                DiagnosticSafeFields.Sha256(lease.DeliveredSha256),
                lease.VerificationEvidenceJson is not null)).ToList(),
            events.Select(entry => new DiagnosticEventSummary(
                entry.Id,
                entry.OccurredAt,
                entry.JobId,
                entry.Attempt,
                entry.LeaseId,
                entry.WorkerId,
                DiagnosticSafeFields.EventReason(entry.ReasonCode),
                DiagnosticSafeFields.Status(entry.PreviousStatus),
                DiagnosticSafeFields.Status(entry.CurrentStatus))).ToList());
    }

    private static int? ResolveAttemptWorkerId(JobAttemptSnapshot attempt, IReadOnlyList<JobLease> leases)
    {
        if (attempt.WorkerName is null)
        {
            return null;
        }

        var matches = leases.Where(lease => lease.Worker?.Name == attempt.WorkerName
                && lease.AcquiredAt <= attempt.EndedAt
                && (attempt.StartedAt is null || lease.EndedAt is null
                    || lease.EndedAt >= attempt.StartedAt))
            .Select(lease => lease.WorkerId)
            .Distinct()
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static DiagnosticReportSummary? SummariseReport(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumStoredReportCharacters)
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<VerificationReport>(json, ReportOptions);
            return report is null ? null : new DiagnosticReportSummary(
                report.Passed,
                report.Checks.Take(MaximumChecksPerReport).Select(check => new DiagnosticCheckSummary(
                    DiagnosticSafeFields.CheckName(check.Name), check.Outcome.ToString())).ToList(),
                report.Vmaf?.Scores is { } scores
                    ? new DiagnosticVmafScores(Finite(scores.VmafMean), Finite(scores.VmafHarmonicMean),
                        Finite(scores.VmafFifthPercentile), Finite(scores.VmafMin), scores.FrameCount)
                    : null,
                DiagnosticSafeFields.Encoder(report.Context?.VideoEncoder),
                DiagnosticSafeFields.VerificationLocation(report.Context?.VerificationLocation));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double? Finite(double? value) =>
        value is { } number && double.IsFinite(number) ? number : null;
}
