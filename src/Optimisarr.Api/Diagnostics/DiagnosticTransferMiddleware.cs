using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Workers;
using Optimisarr.Data;

namespace Optimisarr.Api.Diagnostics;

/// <summary>Records bounded transfer acknowledgements after the authenticated operation completes.</summary>
internal static class DiagnosticTransferMiddleware
{
    public static void UseDiagnosticTransferCapture(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            await next();
            var segments = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments is not { Length: >= 5 } || segments[0] != "api" || segments[1] != "workers" || segments[2] != "leases"
                || !Guid.TryParse(segments[3], out var leaseId) || segments[4] is not ("source" or "result")) return;
            if (context.Response.StatusCode is 401 or 403) return;
            try
            {
                await using var scope = app.Services.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<DiagnosticCaptureStore>();
                var now = DateTimeOffset.UtcNow;
                if (await store.GetActiveAsync(now, CancellationToken.None) is null) return;
                var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
                var worker = await WorkerAuth.ResolveAsync(context.Request, db, CancellationToken.None);
                if (worker is null) return;
                var lease = await db.JobLeases.AsNoTracking().Include(l => l.Job)
                    .FirstOrDefaultAsync(l => l.Id == leaseId && l.WorkerId == worker.Id);
                if (lease is null) return;
                var reason = segments[4] == "source" ? "Transfer.SourceServed" : segments.Length > 5 && segments[5] == "complete"
                    ? "Transfer.ResultAcknowledged" : segments.Length > 5 && segments[5] == "offset" ? "Transfer.OffsetReported" : "Transfer.ChunkReceived";
                await OptimisarrDbContext.DiagnosticTransitionGate.WaitAsync();
                try
                {
                    await DiagnosticEventCapture.AppendAsync(db, new DiagnosticEvent
                    {
                        JobId = lease.JobId, Attempt = lease.ExecutionAttempt, LeaseId = leaseId, WorkerId = worker.Id,
                        ReasonCode = reason, CurrentStatus = lease.Job?.Status.ToString() ?? "Unknown",
                        DetailsJson = JsonSerializer.Serialize(new
                        {
                            httpStatus = context.Response.StatusCode,
                            offsetBytes = long.TryParse(context.Request.Headers["X-Optimisarr-Offset"], out var offset) && offset >= 0 ? offset : (long?)null,
                            transferBytes = context.Request.Method == "GET" ? context.Response.ContentLength : context.Request.ContentLength
                        })
                    }, now, CancellationToken.None);
                    await db.SaveChangesAsync();
                }
                finally { OptimisarrDbContext.DiagnosticTransitionGate.Release(); }
            }
            catch (Exception error)
            {
                app.Logger.LogWarning("Diagnostic transfer capture unavailable: {ErrorType}", error.GetType().Name);
            }
        });
    }
}
