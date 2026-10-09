using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Diagnostics;
using Optimisarr.Data;

namespace Optimisarr.Api.Diagnostics;

/// <summary>Authenticates attribution before accepting bounded, replay-safe sidecar records.</summary>
public sealed class SidecarDiagnosticStore(OptimisarrDbContext db)
{
    public async Task<long> AppendAsync(int workerId, SidecarDiagnosticBatch batch, DateTimeOffset now, CancellationToken token)
    {
        if (batch.DroppedEvents is < 0 or > 1_000_000_000 || batch.SchemaVersion != 1 || batch.InstanceId == Guid.Empty || batch.Events is null || batch.Events.Count > 100
            || batch.Events.Any(e => e is null || e.Sequence <= 0 || !DiagnosticTelemetry.Reasons.Contains(e.ReasonCode))
            || batch.Events.Select(e => e.Sequence).Distinct().Count() != batch.Events.Count)
            throw new ArgumentException("Unsupported diagnostic schema or invalid bounded event batch.");
        await OptimisarrDbContext.DiagnosticTransitionGate.WaitAsync(token);
        try
        {
            var session = await db.DiagnosticCaptureSessions.FirstOrDefaultAsync(s => s.Id == batch.SessionId, token);
            // A short final-upload window accepts records already captured under explicit consent.
            if (session is null || !DiagnosticCapturePolicy.IsRunning(session.StartedAt, session.ExpiresAt, session.StoppedAt, now)
                && ((session.StoppedAt ?? session.ExpiresAt) is not { } end || now > end.AddSeconds(90))) return 0;
            var ids = batch.Events.Select(e => e.LeaseId).Distinct().ToArray();
            var leases = await db.JobLeases.AsNoTracking().Include(l => l.Job).Where(l => ids.Contains(l.Id) && l.WorkerId == workerId).ToListAsync(token);
            if (batch.Events.Any(e => !leases.Any(l => l.Id == e.LeaseId && l.JobId == e.JobId)
                || session.ScopedJobId is { } jobId && jobId != e.JobId))
                throw new ArgumentException("Diagnostic events must belong to this worker's leases and the selected job.");
            long acknowledged = 0;
            foreach (var raw in batch.Events.OrderBy(e => e.Sequence))
            {
                var exists = await db.DiagnosticEvents.AsNoTracking().AnyAsync(e => e.SessionId == session.Id && e.WorkerId == workerId
                    && e.InstanceId == batch.InstanceId && e.SourceSequence == raw.Sequence, token);
                if (!exists)
                {
                    var safe = DiagnosticTelemetry.Sanitize(raw);
                    var lease = leases.Single(l => l.Id == raw.LeaseId);
                    await DiagnosticEventCapture.AppendAsync(db, new DiagnosticEvent
                    {
                        SessionId = session.Id, JobId = safe.JobId, LeaseId = safe.LeaseId, WorkerId = workerId,
                        Attempt = lease.ExecutionAttempt, Source = "Sidecar", InstanceId = batch.InstanceId,
                        SourceSequence = safe.Sequence, OccurredAt = safe.OccurredAt, ReasonCode = safe.ReasonCode,
                        CurrentStatus = lease.Job?.Status.ToString() ?? "Unknown", DetailsJson = JsonSerializer.Serialize(safe, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    }, now, token, allowEndedUpload: true);
                }
                // Capped records are acknowledged as dropped; the visible session cap explains the omission.
                acknowledged = raw.Sequence;
            }
            var receipt = await db.DiagnosticWorkerReceipts.FirstOrDefaultAsync(r => r.SessionId == session.Id && r.WorkerId == workerId, token);
            if (receipt is null)
            {
                receipt = new DiagnosticWorkerReceipt { SessionId = session.Id, WorkerId = workerId };
                db.DiagnosticWorkerReceipts.Add(receipt);
            }
            receipt.LastUploadAt = now;
            receipt.DroppedEvents = Math.Max(receipt.DroppedEvents, batch.DroppedEvents);
            if (batch.Final) receipt.FinalUploadAt = now;
            else if (batch.Events.Count > 0) receipt.FinalUploadAt = null;
            await db.SaveChangesAsync(token);
            return acknowledged;
        }
        finally { OptimisarrDbContext.DiagnosticTransitionGate.Release(); }
    }
}
