using System.Text.Json;
using System.Text.Json.Serialization;
using Optimisarr.Core.Diagnostics;
using Optimisarr.Data;

namespace Optimisarr.Api.Diagnostics;

/// <summary>Records changed scheduler decisions once per explicit session, rather than every poll.</summary>
internal sealed class DiagnosticSchedulingCapture
{
    private Guid lastSession;
    private DiagnosticSchedulingSnapshot? lastSnapshot;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
    public async Task RecordAsync(OptimisarrDbContext db, DiagnosticSchedulingSnapshot snapshot, DateTimeOffset now, CancellationToken token)
    {
        var session = await new DiagnosticCaptureStore(db).GetActiveAsync(now, token);
        if (session is null || session.EventLimitReached) return;
        if (lastSession == session.Id && lastSnapshot == snapshot) return;
        await using var write = await db.BeginDiagnosticWriteAsync(token);
        var appended = await DiagnosticEventCapture.AppendAsync(db, new DiagnosticEvent
            {
                SessionId = session.Id, JobId = session.ScopedJobId ?? 0, ReasonCode = "Queue.DispatchDecision", CurrentStatus = "Unknown",
                DetailsJson = JsonSerializer.Serialize(new { scheduling = snapshot }, Json)
            }, now, token);
        await db.SaveChangesAsync(token);
        await write.CommitAsync(token);
        if (appended) { lastSession = session.Id; lastSnapshot = snapshot; }
    }
}
