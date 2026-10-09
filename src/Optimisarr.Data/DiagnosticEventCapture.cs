using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Diagnostics;

namespace Optimisarr.Data;

/// <summary>Stages bounded evidence in the same transaction as operational state.</summary>
public static class DiagnosticEventCapture
{
    public static async Task<bool> AppendJobTransitionAsync(OptimisarrDbContext db, Job job,
        JobStatus previousStatus, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        if (job.Status == previousStatus || !await IsCapturingAsync(db, job.Id, nowUtc, cancellationToken)) return false;
        var reason = job.Status == JobStatus.Failed && job.FailureCategory is { } category ? $"Failure.{category}"
            : job.Status == JobStatus.Queued && job.RetryReason == "SoftwareDecode" ? "Retry.SoftwareDecode" : "Job.StatusChanged";
        var lease = db.ChangeTracker.Entries<JobLease>().Where(e => e.State != EntityState.Deleted && e.Entity.JobId == job.Id)
            .Select(e => e.Entity).OrderByDescending(l => l.AcquiredAt).FirstOrDefault();
        if (lease is null && (job.Status is JobStatus.Leased or JobStatus.AwaitingVerification || previousStatus is JobStatus.Leased or JobStatus.AwaitingVerification))
            lease = (await db.JobLeases.AsNoTracking().Where(l => l.JobId == job.Id).ToListAsync(cancellationToken)).OrderByDescending(l => l.AcquiredAt).FirstOrDefault();
        var library = (job.LibraryId is { } libraryId
            ? await db.Libraries.AsNoTracking().FirstOrDefaultAsync(l => l.Id == libraryId, cancellationToken) : null);
        return await AppendAsync(db, new DiagnosticEvent
        {
            JobId = job.Id, Attempt = job.ExecutionAttempt, LeaseId = lease?.Id, WorkerId = lease?.WorkerId,
            ReasonCode = reason, PreviousStatus = previousStatus.ToString(), CurrentStatus = job.Status.ToString(),
            DetailsJson = JsonSerializer.Serialize(new
            {
                attemptId = $"{job.Id}:{job.ExecutionAttempt}",
                serverVersion = typeof(DiagnosticEventCapture).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                encoder = SafeEncoder(job.VideoEncoder), decoder = SafeDecoder(lease?.HardwareDecoder),
                job.EffectiveVideoQuality, job.RequestedVideoQuality, job.PreferSoftwareDecode,
                parentAttempt = job.ExecutionAttempt > 1 ? job.ExecutionAttempt - 1 : (int?)null,
                sourceSha256 = DiagnosticTelemetry.Sha256(job.SourceSha256),
                verifiedSourceSha256 = DiagnosticTelemetry.Sha256(job.VerifiedSourceSha256),
                candidateSha256 = DiagnosticTelemetry.Sha256(job.VerifiedOutputSha256), job.OutputSizeBytes, job.VerificationPassed,
                reportSha256 = Hash(job.VerificationReportJson), commandSha256 = Hash(job.FfmpegArguments),
                verificationLocation = lease is null ? "Server" : "Worker",
                report = DiagnosticVerificationSnapshot.Read(job.VerificationReportJson),
                process = DiagnosticProcessSummary.Read(job.ProcessLog),
                policy = library is null ? null : new Dictionary<string, object?>
                {
                    ["durationTolerancePercent"] = library.DurationTolerancePercent,
                    ["requireAudioRetained"] = library.RequireAudioRetained, ["requireSubtitlesRetained"] = library.RequireSubtitlesRetained,
                    ["requireSizeReduction"] = library.RequireSizeReduction, ["minimumSizeSavingPercent"] = library.MinimumSizeSavingPercent,
                    ["maximumSizeSavingPercent"] = library.MaximumSizeSavingPercent, ["vmafQualityGateEnabled"] = library.VmafQualityGateEnabled,
                    ["minVmafHarmonicMean"] = library.MinVmafHarmonicMean, ["minVmafMin"] = library.MinVmafMin,
                    ["minVmafCatastrophicMin"] = library.MinVmafCatastrophicMin, ["vmafFrameSubsample"] = library.VmafFrameSubsample,
                    ["audioQualityReportingEnabled"] = library.AudioQualityReportingEnabled, ["audioQualityGateEnabled"] = library.AudioQualityGateEnabled,
                    ["maximumAudioQualityDistance"] = library.MaximumAudioQualityDistance,
                    ["soundtrackQualityReportingEnabled"] = library.SoundtrackQualityReportingEnabled, ["soundtrackQualityGateEnabled"] = library.SoundtrackQualityGateEnabled,
                    ["maximumSoundtrackQualityDistance"] = library.MaximumSoundtrackQualityDistance, ["autoReplace"] = library.AutoReplace,
                    ["imageQualityGateEnabled"] = library.ImageQualityGateEnabled, ["minimumImageSsim"] = library.MinimumImageSsim,
                    ["imageMetadataGateEnabled"] = library.ImageMetadataGateEnabled
                }
            }, Json)
        }, nowUtc, cancellationToken);
    }

    public static async Task AppendArchivedAttemptsAsync(OptimisarrDbContext db, Job job, string? previousHistory,
        DateTimeOffset now, CancellationToken token)
    {
        if (!await IsCapturingAsync(db, job.Id, now, token)) return;
        var previous = DiagnosticArchivedAttempt.Read(previousHistory).Select(a => a.Number).ToHashSet();
        var leases = await db.JobLeases.AsNoTracking().Where(l => l.JobId == job.Id).ToListAsync(token);
        foreach (var attempt in DiagnosticArchivedAttempt.Read(job.AttemptHistoryJson).Where(a => !previous.Contains(a.Number)))
        {
            var lease = leases.Where(l => l.ExecutionAttempt == attempt.Number).OrderByDescending(l => l.AcquiredAt).FirstOrDefault();
            await AppendAsync(db, new DiagnosticEvent
            {
                JobId = job.Id, Attempt = attempt.Number, LeaseId = lease?.Id, WorkerId = lease?.WorkerId,
                ReasonCode = "Attempt.Archived", CurrentStatus = attempt.VerificationPassed == false ? "Failed" : "Unknown",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    attemptId = $"{job.Id}:{attempt.Number}", parentAttempt = attempt.Number > 1 ? attempt.Number - 1 : (int?)null,
                    encoder = SafeEncoder(attempt.VideoEncoder), decoder = SafeDecoder(attempt.HardwareDecoder),
                    outcome = attempt.Outcome is "Rejected" or "Failed" or "Completed" ? attempt.Outcome : "Unknown",
                    retryReason = attempt.Reason is "HardwareDecodeCorruption" or "ManualRetry" ? attempt.Reason : "Unclassified",
                    report = DiagnosticVerificationSnapshot.Read(attempt.VerificationReportJson), process = DiagnosticProcessSummary.Read(attempt.ProcessLog), attempt.VerificationPassed,
                    reportSha256 = Hash(attempt.VerificationReportJson), commandSha256 = Hash(attempt.FfmpegArguments)
                }, Json)
            }, now, token);
        }
    }

    public static async Task AppendLeaseAsync(OptimisarrDbContext db, JobLease lease, DateTimeOffset now, CancellationToken token)
    {
        if (!await IsCapturingAsync(db, lease.JobId, now, token)) return;
        var worker = db.ChangeTracker.Entries<Worker>().Where(e => e.Entity.Id == lease.WorkerId).Select(e => e.Entity).FirstOrDefault()
            ?? await db.Workers.AsNoTracking().FirstOrDefaultAsync(w => w.Id == lease.WorkerId, token);
        var job = db.ChangeTracker.Entries<Job>().Where(e => e.Entity.Id == lease.JobId).Select(e => e.Entity).FirstOrDefault()
            ?? await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == lease.JobId, token);
        await AppendAsync(db, new DiagnosticEvent
        {
            JobId = lease.JobId, Attempt = lease.ExecutionAttempt, LeaseId = lease.Id, WorkerId = lease.WorkerId,
            ReasonCode = "Lease.Updated", CurrentStatus = job?.Status.ToString() ?? "Unknown",
            DetailsJson = JsonSerializer.Serialize(new
            {
                identitySource = db.Entry(lease).State == EntityState.Added ? "AttemptTimeSnapshot" : "EventTimeSnapshot", sidecarVersion = SafeVersion(worker?.SidecarVersion),
                operatingSystem = worker?.OperatingSystem is "macos" or "windows" or "linux" ? worker.OperatingSystem : "unknown",
                architecture = worker?.Architecture is "arm64" or "x64" or "x86" ? worker.Architecture : "unknown",
                protocolVersion = worker?.ProtocolVersion, encoders = worker?.VideoEncoders.Split(',').Select(SafeEncoder).Where(e => e is not null).Take(20),
                encoder = SafeEncoder(job?.VideoEncoder), decoder = SafeDecoder(lease.HardwareDecoder),
                state = lease.State.ToString(), stage = lease.Stage?.ToString(), endReason = lease.EndReason?.ToString(),
                workSha256 = Hash(lease.VerificationWorkJson), contractSha256 = Hash(lease.VerificationContractJson),
                evidenceSha256 = Hash(lease.VerificationEvidenceJson), qualityContractSha256 = Hash(lease.QualityContractJson),
                evidence = DiagnosticExecutionEvidence.Read(lease.VerificationContractJson, lease.VerificationEvidenceJson),
                candidateSha256 = DiagnosticTelemetry.Sha256(lease.DeliveredSha256),
                sourceSha256 = DiagnosticTelemetry.Sha256(lease.QualitySourceSha256), lease.MaxCandidateBytes, lease.MinCandidateBytes
            }, Json)
        }, now, token);
    }

    public static async Task AppendReplacementAsync(OptimisarrDbContext db, Replacement replacement, DateTimeOffset now, CancellationToken token)
    {
        if (!await IsCapturingAsync(db, replacement.JobId, now, token)) return;
        var job = db.ChangeTracker.Entries<Job>().Where(e => e.Entity.Id == replacement.JobId).Select(e => e.Entity).FirstOrDefault()
            ?? await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == replacement.JobId, token);
        await AppendAsync(db, new DiagnosticEvent
        {
            JobId = replacement.JobId, Attempt = job?.ExecutionAttempt ?? 0, ReasonCode = $"Replacement.{replacement.Status}",
            CurrentStatus = job?.Status.ToString() ?? "Unknown",
            DetailsJson = JsonSerializer.Serialize(new { replacement.OriginalSizeBytes, replacement.NewSizeBytes, replacement.CrossFilesystem,
                originalSha256 = DiagnosticTelemetry.Sha256(replacement.OriginalSha256), outputSha256 = DiagnosticTelemetry.Sha256(replacement.OutputSha256) }, Json)
        }, now, token);
    }

    public static async Task<bool> AppendAsync(OptimisarrDbContext db, DiagnosticEvent entry, DateTimeOffset now, CancellationToken token, bool allowEndedUpload = false)
    {
        var sessions = await db.DiagnosticCaptureSessions.Where(s => (s.StoppedAt == null || allowEndedUpload && s.Id == entry.SessionId) && (s.ScopedJobId == null || s.ScopedJobId == entry.JobId)).ToListAsync(token);
        foreach (var candidate in sessions.Where(s => db.Entry(s).State == EntityState.Unchanged))
            await db.Entry(candidate).ReloadAsync(token);
        var session = sessions.FirstOrDefault(s => DiagnosticCapturePolicy.AllowsEvent(s.StartedAt, s.ExpiresAt, s.StoppedAt, s.ScopedJobId, entry.JobId, now)
            || allowEndedUpload && s.Id == entry.SessionId && (s.StoppedAt ?? s.ExpiresAt) is { } end && now <= end.AddSeconds(90));
        if (session is null || entry.SessionId != Guid.Empty && session.Id != entry.SessionId) return false;
        if (entry.DetailsJson?.Length > 4096)
        {
            var details = System.Text.Json.Nodes.JsonNode.Parse(entry.DetailsJson)!.AsObject();
            details.Remove("policy"); details["detailsTruncated"] = true;
            foreach (var field in new[] { "process", "evidence", "report" })
                if (details.ToJsonString().Length > 4096) details.Remove(field);
            entry.DetailsJson = details.ToJsonString();
        }
        var bytes = Encoding.UTF8.GetByteCount(entry.DetailsJson ?? "") + 512;
        if (session.EventsStored >= DiagnosticCapturePolicy.MaximumEvents || session.BytesStored + bytes > session.MaximumBytes)
        { session.EventLimitReached = true; return false; }
        entry.SessionId = session.Id; entry.ReceivedAt = now;
        if (entry.Source == "Server") entry.OccurredAt = now;
        db.DiagnosticEvents.Add(entry); session.EventsStored++; session.BytesStored += bytes;
        session.HasFailure |= entry.ReasonCode.StartsWith("Failure.", StringComparison.Ordinal) || entry.CurrentStatus == "Failed";
        return true;
    }

    private static async Task<bool> IsCapturingAsync(OptimisarrDbContext db, int jobId, DateTimeOffset now, CancellationToken token) =>
        (await db.DiagnosticCaptureSessions.AsNoTracking().Where(s => s.StoppedAt == null && (s.ScopedJobId == null || s.ScopedJobId == jobId)).ToListAsync(token))
            .Any(s => DiagnosticCapturePolicy.IsRunning(s.StartedAt, s.ExpiresAt, s.StoppedAt, now));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string? Hash(string? text) => text is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string? SafeEncoder(string? value) => value is "libx264" or "libx265" or "libsvtav1" or "h264_nvenc" or "hevc_nvenc" or "av1_nvenc" or "h264_qsv" or "hevc_qsv" or "av1_qsv" or "h264_vaapi" or "hevc_vaapi" or "av1_vaapi" or "h264_videotoolbox" or "hevc_videotoolbox" ? value : null;
    private static string? SafeDecoder(string? value) => value is "cuda" or "videotoolbox" or "qsv" or "d3d11va" or "dxva2" or "vaapi" ? value : null;
    private static string? SafeVersion(string? value) => value is { Length: <= 64 } && System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}(?:\+[0-9a-f]{7,40}| \([0-9]{1,12}\))?\z") ? value : null;
}
