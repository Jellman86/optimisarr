using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Data;

namespace Optimisarr.Api.Diagnostics;

internal sealed record DiagnosticParticipant(int WorkerId, string OperatingSystem, string? Version, string State, int MirroredEvents, long DroppedEvents, DateTimeOffset? LastUploadAt, DateTimeOffset? FinalUploadAt);
internal static class DiagnosticSessionBundleQueries
{
    public const int MaximumBundleBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static async Task<IReadOnlyList<DiagnosticParticipant>> ParticipantsAsync(OptimisarrDbContext db, Guid sessionId, DateTimeOffset now, CancellationToken token)
    {
        var session = await db.DiagnosticCaptureSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, token)
            ?? throw new KeyNotFoundException("Diagnostic session not found.");
        var workers = (await db.Workers.AsNoTracking().ToListAsync(token)).Where(w => w.PairedAt <= (session.StoppedAt ?? session.ExpiresAt ?? now) && (w.RevokedAt == null || w.RevokedAt >= session.StartedAt)).ToList();
        var counts = await db.DiagnosticEvents.AsNoTracking().Where(e => e.SessionId == sessionId && e.Source == "Sidecar" && e.WorkerId != null)
            .GroupBy(e => e.WorkerId).Select(g => new { Id = g.Key, Count = g.Count() }).ToListAsync(token);
        var receipts = await db.DiagnosticWorkerReceipts.AsNoTracking().Where(r => r.SessionId == sessionId).ToListAsync(token);
        var omissions = await db.DiagnosticEvents.AsNoTracking().Where(e => e.SessionId == sessionId && e.WorkerId != null
            && (e.ReasonCode == "Worker.LeaseEvidenceUnavailable" || e.ReasonCode == "Worker.JournalRecoveryIncomplete"))
            .Select(e => e.WorkerId).Distinct().ToListAsync(token);
        return workers.OrderByDescending(w => receipts.Any(r => r.WorkerId == w.Id)).ThenByDescending(w => w.LastSeenAt).ThenByDescending(w => w.Id).Select(w => new DiagnosticParticipant(w.Id, DiagnosticSafeFields.OperatingSystem(w.OperatingSystem),
            DiagnosticSafeFields.Version(w.SidecarVersion), receipts.Any(r => r.WorkerId == w.Id && r.FinalUploadAt != null) ? session.EventLimitReached || omissions.Contains(w.Id) ? "CollectedWithOmissions" : receipts.Any(r => r.WorkerId == w.Id && r.DroppedEvents > 0) ? "CollectedWithLocalOmissions" : "Collected" : w.ProtocolVersion < 10 ? "SidecarUpdateRequiredForLocalCapture" : w.LastSeenAt is null || now - w.LastSeenAt > TimeSpan.FromMinutes(2)
                ? "OfflineLocalEvidenceUnavailable" : counts.Any(c => c.Id == w.Id) ? "MirroredLocalEvidenceMayBePending" : "NoLocalEvidenceReceived",
            counts.FirstOrDefault(c => c.Id == w.Id)?.Count ?? 0,
            receipts.FirstOrDefault(r => r.WorkerId == w.Id)?.DroppedEvents ?? 0,
            receipts.FirstOrDefault(r => r.WorkerId == w.Id)?.LastUploadAt,
            receipts.FirstOrDefault(r => r.WorkerId == w.Id)?.FinalUploadAt)).ToList();
    }
    public static async Task<byte[]> BuildAsync(OptimisarrDbContext db, Guid sessionId, int? workerId, DateTimeOffset? from, DateTimeOffset? to,
        DateTimeOffset now, CancellationToken token)
    {
        if (from > to) throw new ArgumentException("The start of the time range must precede its end.");
        var session = await db.DiagnosticCaptureSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, token)
            ?? throw new KeyNotFoundException("Diagnostic session not found.");
        var query = db.DiagnosticEvents.AsNoTracking().Where(e => e.SessionId == sessionId && (workerId == null || e.WorkerId == workerId));
        // DateTimeOffset comparisons are evaluated after the session's bounded 10,000 records are read.
        var events = (await query.OrderBy(e => e.Id).ToListAsync(token)).Where(e => (from is null || e.ReceivedAt >= from) && (to is null || e.ReceivedAt <= to)).ToList();
        var jobs = new List<DiagnosticJobBundle>();
        var omissions = new List<string>();
        var remainingBytes = MaximumBundleBytes - 256 * 1024;
        var jobIds = events.Where(e => e.JobId > 0).Select(e => e.JobId).Concat(session.ScopedJobId is { } scoped ? new[] { scoped } : []).Distinct().ToArray();
        if (jobIds.Length > 200) omissions.Add("Job summaries are limited to 200; remaining captured records stay in events.");
        foreach (var id in jobIds.Take(200))
        {
            if (!await db.Jobs.AsNoTracking().AnyAsync(j => j.Id == id, token)) { omissions.Add($"Job {id} no longer exists; its bounded timeline remains in events."); continue; }
            var job = await DiagnosticJobBundleQueries.BuildAsync(db, sessionId, id, now, token);
            job = job with { Events = [] };
            var size = ItemBytes(job);
            if (size > remainingBytes) { omissions.Add("Older job summaries omitted to keep bundle below 8 MiB."); break; }
            jobs.Add(job); remainingBytes -= size;
        }
        var summaries = new List<DiagnosticEventSummary>();
        foreach (var e in events)
        {
            var value = new DiagnosticEventSummary(e.Id, e.OccurredAt, e.JobId, e.Attempt, e.LeaseId, e.WorkerId,
                DiagnosticSafeFields.EventReason(e.ReasonCode), DiagnosticSafeFields.Status(e.PreviousStatus), DiagnosticSafeFields.Status(e.CurrentStatus),
                e.Source == "Sidecar" ? "Sidecar" : "Server", e.ReceivedAt, e.InstanceId, e.SourceSequence, DiagnosticDetailSanitizer.Read(e.DetailsJson));
            var size = ItemBytes(value);
            if (size > remainingBytes) { omissions.Add("Later events omitted to keep bundle below 8 MiB. Select a narrower time range."); break; }
            summaries.Add(value); remainingBytes -= size;
        }
        omissions.Add("Worker.LeaseEvidenceUnavailable marks records whose lease was deleted. Worker.JournalRecoveryIncomplete means the local journal could not be fully recovered; the number of lost records is unknown.");
        omissions.Add("Sidecar-local records are mirrored on check-in. Offline or unacknowledged records may require a local export.");
        omissions.Add("Raw process text, commands, media payloads and credentials are excluded. Server receipt IDs order events; local clocks may differ.");
        var participants = await ParticipantsAsync(db, sessionId, now, token);
        if (participants.Count > 100) omissions.Add("Participant summaries are limited to 100; the participants endpoint lists all eligible workers.");
        var result = JsonSerializer.SerializeToUtf8Bytes(new
        {
            manifest = new DiagnosticBundleManifest(4, sessionId, now, session.IncludePaths, session.EventLimitReached, omissions,
                DiagnosticManifestIdentifier.For(sessionId, $"session:{workerId}:{from?.ToUniversalTime():O}:{to?.ToUniversalTime():O}", events.Select(e => e.Id)), participants.Take(100).ToList()),
            range = new { from, to, workerId }, jobs, events = summaries
        }, Json);
        if (result.Length > MaximumBundleBytes) throw new ArgumentException("Select a narrower diagnostic time range.");
        return result;
    }
    private static int ItemBytes<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        // Array entries gain four spaces on every line when nested in the exported document.
        return bytes.Length + 4 * (bytes.Count(b => b == (byte)'\n') + 1) + 2;
    }

}
