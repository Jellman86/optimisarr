using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Api.Diagnostics;
using Optimisarr.Api.Library;
using Optimisarr.Core.Diagnostics;
using Optimisarr.Core.Workers;
using Optimisarr.Data;
namespace Optimisarr.Tests;

[Collection(TokenedApiCollection.Name)]
public sealed class DiagnosticCaptureEndpointTests(AdminTokenAuthEndpointTests.TokenedApi api)
{
    [Fact]
    public async Task Real_routes_enforce_worker_credentials_and_consent_then_export_replayed_events_with_retention_controls()
    {
        using var admin = api.CreateClient(); admin.DefaultRequestHeaders.Authorization = new("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        using var workerClient = api.CreateClient();
        using var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/diagnostics/captures")).StatusCode);
        Assert.Contains((await anonymous.PostAsJsonAsync("/api/workers/diagnostics", new SidecarDiagnosticBatch(1, Guid.NewGuid(), Guid.NewGuid(), []))).StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        int jobId, workerId; Guid leaseId = Guid.NewGuid(); var credential = "diagnostic-worker-test-credential";
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
            await settings.SetQueueSettingsAsync((await settings.GetQueueSettingsAsync(default)) with { RemoteWorkersEnabled = true }, default);
            var worker = new Worker { Name = "Diagnostic test", OperatingSystem = "linux", Architecture = "x64", ProtocolVersion = 10, CredentialFingerprint = WorkerCredential.Fingerprint(credential) };
            var media = new MediaFile { Path = Path.Combine(api.LibraryDirectory, Guid.NewGuid()+".mkv"), RelativePath = "private-title.mkv" };
            db.Workers.Add(worker); db.MediaFiles.Add(media); await db.SaveChangesAsync();
            var job = new Job { MediaFileId = media.Id, Status = JobStatus.Failed, ExecutionAttempt = 1 }; db.Jobs.Add(job); await db.SaveChangesAsync();
            jobId = job.Id; workerId = worker.Id;
            db.JobLeases.Add(new() { Id = leaseId, JobId = jobId, WorkerId = workerId, ExecutionAttempt = 1, AcquiredAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5), State = LeaseState.Completed });
            await db.SaveChangesAsync();
        }
        workerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        using var start = await admin.PostAsJsonAsync("/api/diagnostics/capture", new { durationHours = 1, scopedJobId = jobId, includePaths = false, retentionDays = 1, failureRetentionDays = 2 });
        start.EnsureSuccessStatusCode(); var capture = await start.Content.ReadFromJsonAsync<JsonElement>(); var id = capture.GetProperty("id").GetGuid();
        try
        {
            var heartbeat = await workerClient.PostAsJsonAsync("/api/workers/heartbeat", new { freeScratchBytes = 1024, maxConcurrency = 0, protocolMinimum = 1, protocolMaximum = 10 });
            heartbeat.EnsureSuccessStatusCode(); var beat = await heartbeat.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(id, beat.GetProperty("diagnosticCapture").GetProperty("sessionId").GetGuid());
            var batch = new SidecarDiagnosticBatch(1, id, Guid.NewGuid(), [new(1, DateTimeOffset.UtcNow.AddHours(2), leaseId, jobId, "Worker.StageChanged", "Encoding", 1)]);
            (await workerClient.PostAsJsonAsync("/api/workers/diagnostics", batch)).EnsureSuccessStatusCode();
            (await workerClient.PostAsJsonAsync("/api/workers/diagnostics", batch)).EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.BadRequest, (await workerClient.PostAsJsonAsync("/api/workers/diagnostics", batch with { SchemaVersion = 999 })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await workerClient.PostAsJsonAsync("/api/workers/diagnostics", batch with { Events = [batch.Events[0] with { LeaseId = Guid.NewGuid() }] })).StatusCode);
            var json = await admin.GetStringAsync($"/api/diagnostics/capture/{id}/bundle?workerId={workerId}");
            using var bundle = JsonDocument.Parse(json); Assert.Equal(1, bundle.RootElement.GetProperty("events").GetArrayLength());
            Assert.DoesNotContain("private-title", json); Assert.DoesNotContain(credential, json); Assert.Contains("Sidecar", json);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/diagnostics/capture/{id}/bundle?fromUtc=2026-10-10T00:00:00Z&toUtc=2026-10-01T00:00:00Z")).StatusCode);
            var matching = await admin.GetFromJsonAsync<JsonElement>($"/api/diagnostics/captures?jobId={jobId}");
            Assert.Contains(matching.EnumerateArray(), item => item.GetProperty("id").GetGuid() == id);
            Assert.Equal(0, (await admin.GetFromJsonAsync<JsonElement>("/api/diagnostics/captures?jobId=2147483647")).GetArrayLength());
            (await admin.PostAsync($"/api/diagnostics/capture/{id}/stop", null)).EnsureSuccessStatusCode();
            var finalHeartbeat = await workerClient.PostAsJsonAsync("/api/workers/heartbeat", new { freeScratchBytes = 1024, maxConcurrency = 0, protocolMinimum = 1, protocolMaximum = 10 });
            var finalConsent = (await finalHeartbeat.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("diagnosticCapture");
            Assert.False(finalConsent.GetProperty("recording").GetBoolean());
            (await workerClient.PostAsJsonAsync("/api/workers/diagnostics", batch with { Events = [batch.Events[0] with { Sequence = 2 }] })).EnsureSuccessStatusCode();
            (await workerClient.PostAsJsonAsync("/api/workers/diagnostics", batch with { Events = [], Final = true })).EnsureSuccessStatusCode();
            var participants = await admin.GetFromJsonAsync<JsonElement>($"/api/diagnostics/capture/{id}/participants");
            Assert.Contains(participants.EnumerateArray(), p => p.GetProperty("workerId").GetInt32() == workerId && p.GetProperty("state").GetString() == "Collected");
            (await admin.PutAsJsonAsync($"/api/diagnostics/capture/{id}/pin", new { pinned = true })).EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/diagnostics/capture/{id}")).StatusCode);
        }
        finally
        {
            await admin.PostAsync($"/api/diagnostics/capture/{id}/stop", null);
            await admin.PutAsJsonAsync($"/api/diagnostics/capture/{id}/pin", new { pinned = false });
            (await admin.DeleteAsync($"/api/diagnostics/capture/{id}")).EnsureSuccessStatusCode();
        }
    }
}
