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
        await OptimisarrDbContext.DiagnosticTransitionGate.WaitAsync(token);
        try
        {
            if (lastSession == session.Id && lastSnapshot == snapshot) return;
            if (await DiagnosticEventCapture.AppendAsync(db, new DiagnosticEvent
                {
                    SessionId = session.Id, JobId = session.ScopedJobId ?? 0, ReasonCode = "Queue.DispatchDecision", CurrentStatus = "Unknown",
                    DetailsJson = JsonSerializer.Serialize(new { scheduling = snapshot }, Json)
                }, now, token))
            {
                lastSession = session.Id; lastSnapshot = snapshot;
            }
            await db.SaveChangesAsync(token);
        }
        finally { OptimisarrDbContext.DiagnosticTransitionGate.Release(); }
    }
}
