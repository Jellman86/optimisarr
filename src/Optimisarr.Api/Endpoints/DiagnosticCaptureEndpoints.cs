using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Diagnostics;
using Optimisarr.Core.Diagnostics;
using Optimisarr.Data;
using Optimisarr.Api.Workers;
using Optimisarr.Api.Library;

namespace Optimisarr.Api.Endpoints;

internal sealed record PinDiagnosticRequest(bool Pinned);

internal sealed record StartDiagnosticCaptureRequest(
    [property: JsonRequired] int? DurationHours,
    int? ScopedJobId,
    bool IncludePaths = false,
    bool PersistAcrossRestart = false,
    int RetentionDays = 7,
    int FailureRetentionDays = 30,
    long MaximumBytes = 4 * 1024 * 1024);

internal sealed record DiagnosticCaptureDto(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? StoppedAt,
    int? ScopedJobId,
    bool IncludePaths,
    int EventsStored,
    int MaximumEvents,
    bool EventLimitReached,
    string Status,
    bool PersistAcrossRestart, bool Pinned, int RetentionDays, int FailureRetentionDays,
    long MaximumBytes, long BytesStored, DateTimeOffset? RetainUntil)
{
    public static DiagnosticCaptureDto From(DiagnosticCaptureSession session, DateTimeOffset nowUtc) => new(
        session.Id,
        session.StartedAt,
        session.ExpiresAt,
        session.StoppedAt,
        session.ScopedJobId,
        session.IncludePaths,
        session.EventsStored,
        DiagnosticCapturePolicy.MaximumEvents,
        session.EventLimitReached,
        DiagnosticCapturePolicy.IsRunning(session.StartedAt, session.ExpiresAt, session.StoppedAt, nowUtc)
            ? "Recording" : session.StoppedAt is not null ? "Stopped" : "Expired",
        session.PersistAcrossRestart, session.Pinned, session.RetentionDays, session.FailureRetentionDays,
        session.MaximumBytes, session.BytesStored,
        session.Pinned ? null : (session.StoppedAt ?? session.ExpiresAt)?.AddDays(session.HasFailure ? session.FailureRetentionDays : session.RetentionDays));
}

internal static class DiagnosticCaptureEndpoints
{
    public static void MapDiagnosticCaptureEndpoints(this WebApplication app)
    {
        app.MapPost("/api/workers/diagnostics", async (SidecarDiagnosticBatch request, HttpRequest http,
            OptimisarrDbContext db, SettingsStore settings, SidecarDiagnosticStore store, CancellationToken token) =>
        {
            if (await WorkerGate.RefusedAsync(settings, token) is { } refused) return refused;
            var worker = await WorkerAuth.ResolveAsync(http, db, token);
            if (worker is null) return Results.Unauthorized();
            try { return Results.Ok(new { acknowledgedSequence = await store.AppendAsync(worker.Id, request, DateTimeOffset.UtcNow, token) }); }
            catch (ArgumentException ex) { return ApiErrors.BadRequest("diagnostics.events.invalid", ex.Message); }
        }).WithName("AppendSidecarDiagnostics").WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(128 * 1024));

        app.MapGet("/api/diagnostics/captures", async (int? jobId, OptimisarrDbContext db, CancellationToken token) =>
        {
            var sessions = await db.DiagnosticCaptureSessions.AsNoTracking()
                .Where(s => jobId == null || s.ScopedJobId == jobId || db.DiagnosticEvents.Any(e => e.SessionId == s.Id && e.JobId == jobId))
                .ToListAsync(token);
            return Results.Ok(sessions.OrderByDescending(s => s.StartedAt).Take(20).Select(s => DiagnosticCaptureDto.From(s, DateTimeOffset.UtcNow)));
        }).WithName("ListDiagnosticCaptures");

        app.MapGet("/api/diagnostics/capture/{id:guid}/participants", async (Guid id, OptimisarrDbContext db, CancellationToken token) =>
        {
            if (!await db.DiagnosticCaptureSessions.AsNoTracking().AnyAsync(s => s.Id == id, token)) return Results.NotFound();
            return Results.Ok(await DiagnosticSessionBundleQueries.ParticipantsAsync(db, id, DateTimeOffset.UtcNow, token));
        }).WithName("GetDiagnosticParticipants");

        app.MapPut("/api/diagnostics/capture/{id:guid}/pin", async (Guid id, PinDiagnosticRequest request, DiagnosticCaptureStore store, CancellationToken token) =>
        {
            try { await store.SetPinnedAsync(id, request.Pinned, token); return Results.Ok(); }
            catch (KeyNotFoundException ex) { return ApiErrors.NotFound("diagnostics.capture.notFound", ex.Message); }
        }).WithName("PinDiagnosticCapture");

        app.MapDelete("/api/diagnostics/capture/{id:guid}", async (Guid id, DiagnosticCaptureStore store, CancellationToken token) =>
        {
            try { return await store.DeleteEndedAsync(id, DateTimeOffset.UtcNow, token) ? Results.NoContent() : Results.NotFound(); }
            catch (InvalidOperationException ex) { return ApiErrors.Conflict("diagnostics.capture.retained", ex.Message); }
        }).WithName("DeleteDiagnosticCapture");

        app.MapGet("/api/diagnostics/capture/{id:guid}/bundle", async (Guid id, int? workerId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc,
            OptimisarrDbContext db, CancellationToken token) =>
        {
            try
            {
                var bundle = await DiagnosticSessionBundleQueries.BuildAsync(db, id, workerId, fromUtc, toUtc, DateTimeOffset.UtcNow, token);
                return Results.File(bundle, "application/json", $"optimisarr-diagnostics-{id:N}.json");
            }
            catch (KeyNotFoundException ex) { return ApiErrors.NotFound("diagnostics.capture.notFound", ex.Message); }
            catch (ArgumentException ex) { return ApiErrors.BadRequest("diagnostics.range.invalid", ex.Message); }
        }).WithName("DownloadDiagnosticSession");
        app.MapGet("/api/diagnostics/capture", async (
            DiagnosticCaptureStore store, CancellationToken cancellationToken) =>
        {
            var latest = await store.GetLatestAsync(cancellationToken);
            return Results.Ok(latest is null ? null : DiagnosticCaptureDto.From(latest, DateTimeOffset.UtcNow));
        }).WithName("GetDiagnosticCapture");

        app.MapPost("/api/diagnostics/capture", async (
            StartDiagnosticCaptureRequest request,
            DiagnosticCaptureStore store,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (!DiagnosticCapturePolicy.IsAllowedDuration(request.DurationHours)
                || request.ScopedJobId is <= 0)
            {
                return ApiErrors.BadRequest("diagnostics.capture.invalid",
                    "Choose 1 hour, 24 hours, 7 days, or until stopped, and a valid job ID if scoped.");
            }

            if (request.ScopedJobId is { } jobId
                && !await db.Jobs.AsNoTracking().AnyAsync(job => job.Id == jobId, cancellationToken))
            {
                return ApiErrors.NotFound("diagnostics.job.notFound", "Job not found.");
            }

            try
            {
                var session = await store.StartAsync(request.DurationHours, request.ScopedJobId,
                    request.IncludePaths, DateTimeOffset.UtcNow, cancellationToken,
                    new DiagnosticCaptureOptions(request.PersistAcrossRestart, request.RetentionDays, request.FailureRetentionDays, request.MaximumBytes));
                return Results.Created($"/api/diagnostics/capture/{session.Id}",
                    DiagnosticCaptureDto.From(session, DateTimeOffset.UtcNow));
            }
            catch (ArgumentException ex)
            {
                return ApiErrors.BadRequest("diagnostics.capture.invalid", ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return ApiErrors.Conflict("diagnostics.capture.active", ex.Message);
            }
        }).WithName("StartDiagnosticCapture");

        app.MapPost("/api/diagnostics/capture/{id:guid}/stop", async (
            Guid id,
            DiagnosticCaptureStore store,
            CancellationToken cancellationToken) =>
        {
            await store.StopAsync(id, DateTimeOffset.UtcNow, cancellationToken);
            var session = await store.GetAsync(id, cancellationToken);
            return session is null
                ? ApiErrors.NotFound("diagnostics.capture.notFound", "Diagnostic capture not found.")
                : Results.Ok(DiagnosticCaptureDto.From(session, DateTimeOffset.UtcNow));
        }).WithName("StopDiagnosticCapture");

        app.MapGet("/api/diagnostics/capture/{id:guid}/jobs/{jobId:int}/bundle", async (
            Guid id,
            int jobId,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var bundle = await DiagnosticJobBundleQueries.BuildAsync(db, id, jobId,
                    DateTimeOffset.UtcNow, cancellationToken);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(bundle, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    WriteIndented = true
                });
                if (bytes.Length > DiagnosticSessionBundleQueries.MaximumBundleBytes)
                    return ApiErrors.Conflict("diagnostics.bundle.tooLarge", "Use a narrower time range in the session export to stay within 8 MiB.");
                return Results.File(bytes, "application/json",
                    $"optimisarr-diagnostics-{jobId}-{id:N}.json");
            }
            catch (KeyNotFoundException ex)
            {
                return ApiErrors.NotFound("diagnostics.bundle.notFound", ex.Message);
            }
        }).WithName("DownloadJobDiagnostics");
    }
}
