using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Realtime;
using Optimisarr.Api.Library;
using Optimisarr.Api.Workers;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Api.Endpoints;

internal sealed record PerceptualProbeDirectionDto(RemotePerceptualSearchStep? NextStep, int? SelectedValue,
    IReadOnlyList<string>? Arguments, bool SourceBlocked, string Reason);

internal static class WorkerPerceptualSearchEndpoints
{
    public static void MapWorkerPerceptualSearchEndpoints(this WebApplication app)
    {
        app.MapPost("/api/workers/leases/{leaseId:guid}/perceptual-probe", async (Guid leaseId, RemotePerceptualProbeReport request,
            HttpRequest http, SettingsStore settings, OptimisarrDbContext db, IHubContext<JobsHub> hub, CancellationToken token) =>
        {
            if (await WorkerGate.RefusedAsync(settings, token) is { } gate) return gate;
            var worker = await WorkerAuth.ResolveAsync(http, db, token, recordSeen: false);
            if (worker is null) return WorkerGate.Unauthenticated();
            var json = JsonSerializer.Serialize(request, PerceptualWorkerWork.Json);
            if (Encoding.UTF8.GetByteCount(json) > 48 * 1024)
                return ApiErrors.BadRequest("worker.perceptual.tooLarge", "Perceptual sample evidence exceeds its report budget.");
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            await using var write = await db.BeginDiagnosticWriteAsync(token);
            await db.Entry(worker).ReloadAsync(token);
            if (worker.CredentialFingerprint is null) return WorkerGate.Unauthenticated();
            if (worker.ProtocolVersion < WorkerProtocol.PerceptualQualityVersion)
                return ApiErrors.Conflict("worker.perceptual.protocol", "This search requires an updated perceptual-search worker.");
            var lease = await db.JobLeases.Include(l => l.Job).ThenInclude(j => j!.MediaFile).SingleOrDefaultAsync(l => l.Id == leaseId, token);
            if (lease is null) return ApiErrors.NotFound("worker.lease.notFound", "The lease no longer exists.");
            if (lease.WorkerId != worker.Id) return Results.Json(new ApiError("worker.lease.notHolder", "This lease belongs to another worker."), statusCode: 403);
            var job = lease.Job;
            if (job is null || job.ExecutionAttempt != lease.ExecutionAttempt)
                return ApiErrors.Conflict("worker.perceptual.attemptEnded", "This lease belongs to an earlier execution attempt.");
            RemotePerceptualSearchState? previous;
            try { previous = lease.PerceptualSearchStateJson is { } stored ? JsonSerializer.Deserialize<RemotePerceptualSearchState>(stored, PerceptualWorkerWork.Json) : null; }
            catch (JsonException) { previous = null; }
            if (previous?.Encoding is null || job.SourceSha256 is not { } source)
                return ApiErrors.Conflict("worker.perceptual.notRequested", "This lease has no complete frozen perceptual search and transferred source.");
            var replayBlocked = previous.SourceRejected && job.Status == JobStatus.Failed
                && lease.State == LeaseState.Released && previous.LastReportSha256 == digest;
            if (!replayBlocked && (job.Status != JobStatus.Leased || lease.ToDomain().StateAt(DateTimeOffset.UtcNow) != LeaseState.Held))
                return ApiErrors.Conflict("worker.lease.notHeld", "The lease no longer accepts sample evidence.");
            var advanced = RemotePerceptualSearchCoordinator.Advance(previous, source, request, digest);
            if (advanced.State is not { } state)
                return ApiErrors.Conflict("worker.perceptual.reportMismatch", advanced.Error ?? "The sample does not match its requested contract.");
            PerceptualProbeDirectionDto direction;
            string? completedWork = null;
            var summary = PerceptualWorkerWork.Summary(state);
            try
            {
                if (state.SourceRejected)
                    direction = new(null, null, null, true, state.SourceError ?? "Required source assessment is unavailable.");
                else if (state.CanEncode)
                {
                    completedWork = PerceptualWorkerWork.Complete(lease.VerificationWorkJson, state, summary);
                    var selected = state.Encoding!.Select(state);
                    direction = new(null, state.Decision!.SelectedValue, selected.Arguments, false, state.Decision.Reason);
                }
                else if (state.AskedValue is { } next)
                    direction = new(RemotePerceptualSearchPlanner.Plan(state.Contract, next, state.Probes.Count, state.Encoding!.Spec),
                        null, null, false, state.Decision?.Reason ?? "Measure the assigned sample.");
                else return ApiErrors.Conflict("worker.perceptual.invalidState", "The stored search has no valid next action.");
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException)
            { return ApiErrors.Conflict("worker.perceptual.invalidState", error.Message); }
            if (!advanced.Duplicate)
            {
                var persisted = JsonSerializer.Serialize(state, PerceptualWorkerWork.Json);
                var report = JsonSerializer.Serialize(summary, PerceptualWorkerWork.Json);
                if (persisted.Length > 262144 || report.Length > 262144)
                    return ApiErrors.BadRequest("worker.perceptual.tooLarge", "The search would exceed its persisted evidence budget.");
                lease.PerceptualSearchStateJson = persisted;
                job.PerceptualSearchJson = report;
                job.UpdatedAt = DateTimeOffset.UtcNow;
                if (state.SourceRejected)
                {
                    var reason = "Required source assessment is unavailable. Full encoding was not started. " + direction.Reason;
                    job.Status = JobStatus.Failed; job.FinishedAt = job.UpdatedAt; job.ErrorMessage = reason;
                    job.VerificationPassed = false; job.FfmpegArguments = null;
                    job.VerificationReportJson = JsonSerializer.Serialize(new VerificationReport(
                        [new("Quality-first source eligibility", CheckOutcome.Failed, reason)], PerceptualSearch: summary), PerceptualWorkerWork.Json);
                    lease.State = LeaseState.Released; lease.EndedAt = job.UpdatedAt;
                    lease.EndReason = LeaseEndReason.SourceAssessmentUnavailable;
                }
                else if (state.CanEncode)
                {
                    lease.VerificationWorkJson = completedWork;
                    if (state.Contract.Kind == MediaKind.Image) job.AdaptiveImageQuality = state.Decision!.SelectedValue;
                    else job.AdaptiveAudioBitrateKbps = state.Decision!.SelectedValue;
                    job.FfmpegArguments = string.Join(' ', direction.Arguments!); job.Progress = 0;
                    lease.Stage = RemoteStage.Encoding;
                }
                else { lease.Stage = RemoteStage.Measuring; job.Progress = Math.Clamp(state.Probes.Count / 4.0, 0.001, 0.999); }
            }
            worker.LastSeenAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(token); await write.CommitAsync(token);
            if (!advanced.Duplicate) await hub.Clients.All.SendAsync("jobsChanged", token);
            return Results.Ok(direction);
        }).WithName("ReportPerceptualProbe").Produces<PerceptualProbeDirectionDto>()
          .Produces<ApiError>(400).Produces<ApiError>(401).Produces<ApiError>(403).Produces<ApiError>(404).Produces<ApiError>(409)
          .WithMetadata(new RequestSizeLimitAttribute(64 * 1024));
    }
}
