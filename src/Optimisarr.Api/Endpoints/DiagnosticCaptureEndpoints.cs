using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Diagnostics;
using Optimisarr.Core.Diagnostics;
using Optimisarr.Data;

namespace Optimisarr.Api.Endpoints;

internal sealed record StartDiagnosticCaptureRequest(
    [property: JsonRequired] int? DurationHours,
    int? ScopedJobId,
    bool IncludePaths = false);

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
    string Status)
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
            ? "Recording" : session.StoppedAt is not null ? "Stopped" : "Expired");
}

internal static class DiagnosticCaptureEndpoints
{
    public static void MapDiagnosticCaptureEndpoints(this WebApplication app)
    {
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
                    request.IncludePaths, DateTimeOffset.UtcNow, cancellationToken);
                return Results.Created($"/api/diagnostics/capture/{session.Id}",
                    DiagnosticCaptureDto.From(session, DateTimeOffset.UtcNow));
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
