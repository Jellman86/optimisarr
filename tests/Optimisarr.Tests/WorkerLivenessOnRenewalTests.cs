using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Tests;

/// <summary>
/// A worker busy with a job is not a worker that has gone away.
///
/// <para>Only the check-in used to say so, and the Windows sidecar runs its job inside its
/// check-in loop — so it stops checking in for as long as a job takes. Anything over the
/// two-minute threshold made a machine that was busily encoding look offline. Its renewals kept
/// working, because those do not test liveness, so jobs finished; but an offline worker is not one
/// the queue holds work for, and the placement preference quietly stopped preferring it while it
/// was doing exactly what it had been asked to.</para>
/// </summary>
[Collection(TokenedApiCollection.Name)]
public sealed class WorkerLivenessOnRenewalTests : IAsyncLifetime
{
    private readonly AdminTokenAuthEndpointTests.TokenedApi _api;
    private readonly List<int> _createdLibraries = [];

    public WorkerLivenessOnRenewalTests(AdminTokenAuthEndpointTests.TokenedApi api) => _api = api;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_createdLibraries.Count == 0) return;
        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var jobIds = await db.Jobs
            .Where(job => job.LibraryId != null && _createdLibraries.Contains(job.LibraryId.Value))
            .Select(job => job.Id).ToListAsync();
        db.JobLeases.RemoveRange(db.JobLeases.Where(lease => jobIds.Contains(lease.JobId)));
        await db.SaveChangesAsync();
        db.Libraries.RemoveRange(db.Libraries.Where(library => _createdLibraries.Contains(library.Id)));
        await db.SaveChangesAsync();
    }

    private HttpClient Admin()
    {
        var client = _api.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        return client;
    }

    private async Task EnableRemoteWorkers()
    {
        var admin = Admin();
        var current = await (await admin.GetAsync("/api/settings")).Content.ReadFromJsonAsync<JsonElement>();
        using var document = JsonDocument.Parse(current.GetRawText());
        var payload = new Dictionary<string, object?>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            payload[property.Name] = JsonSerializer.Deserialize<object?>(property.Value.GetRawText());
        }

        payload["remoteWorkersEnabled"] = true;
        (await admin.PutAsJsonAsync("/api/settings", payload)).EnsureSuccessStatusCode();
    }

    private async Task<(HttpClient Client, int Id)> PairWorker(string name)
    {
        await EnableRemoteWorkers();
        var admin = Admin();
        var pin = (await (await admin.PostAsync("/api/workers/pairing-code", null))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        var paired = await _api.CreateClient().PostAsJsonAsync("/api/workers/pair", new
        {
            code = pin, name, operatingSystem = "windows", architecture = "x64",
            protocolMinimum = 1, protocolMaximum = 1,
            videoEncoders = new[] { "hevc_nvenc" }, audioEncoders = new[] { "aac" },
            hardwareDecoders = Array.Empty<string>(),
            vmaf = "Cpu", freeScratchBytes = 500L * 1024 * 1024 * 1024, maxConcurrency = 1,
        });
        paired.EnsureSuccessStatusCode();
        var body = await paired.Content.ReadFromJsonAsync<JsonElement>();
        var client = _api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", body.GetProperty("credential").GetString()!);
        return (client, body.GetProperty("workerId").GetInt32());
    }

    /// <summary>Puts the worker's last check-in far enough back that it counts as gone.</summary>
    private async Task SilenceSince(int workerId, TimeSpan ago)
    {
        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var worker = await db.Workers.SingleAsync(row => row.Id == workerId);
        worker.LastSeenAt = DateTimeOffset.UtcNow - ago;
        await db.SaveChangesAsync();
    }

    private async Task<DateTimeOffset?> LastSeen(int workerId)
    {
        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        return (await db.Workers.AsNoTracking().SingleAsync(row => row.Id == workerId)).LastSeenAt;
    }

    [Fact]
    public async Task A_worker_that_only_renews_is_still_a_worker_that_is_there()
    {
        var (worker, workerId) = await PairWorker("Busy");
        await SilenceSince(workerId, TimeSpan.FromMinutes(10));

        // A renewal for a lease that does not exist: refused, and that is the point. Even a request
        // the server turns down is this credential speaking, which is all "seen" has ever meant.
        using var renewal = await worker.PostAsJsonAsync(
            $"/api/workers/leases/{Guid.NewGuid()}/renew", new { stage = "Encoding" });

        Assert.NotEqual(HttpStatusCode.OK, renewal.StatusCode);
        var seen = await LastSeen(workerId);
        Assert.NotNull(seen);
        Assert.True(
            DateTimeOffset.UtcNow - seen!.Value < TimeSpan.FromMinutes(1),
            $"expected the worker to have been seen just now, but it was {seen}");
    }

    [Fact]
    public async Task An_unknown_credential_is_nobody_and_marks_nobody_as_seen()
    {
        // The stamp has to sit behind the credential check, or an attacker could keep a revoked
        // worker looking alive by shouting at the renew route.
        var (_, workerId) = await PairWorker("Revoked-ish");
        await SilenceSince(workerId, TimeSpan.FromMinutes(10));
        var before = await LastSeen(workerId);

        var stranger = _api.CreateClient();
        stranger.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "not-a-credential");
        using var response = await stranger.PostAsJsonAsync(
            $"/api/workers/leases/{Guid.NewGuid()}/renew", new { stage = "Encoding" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(before, await LastSeen(workerId));
    }
}
