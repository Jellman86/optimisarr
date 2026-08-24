using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Data;

namespace Optimisarr.Tests;

/// <summary>
/// Claiming work is the first point a remote machine affects the local queue, so the property that
/// matters most is not that claiming works — it is that a claimed job stops being runnable here.
/// Two encoders on one original is the failure this whole mechanism exists to prevent.
/// </summary>
[Collection(TokenedApiCollection.Name)]
public sealed class WorkerLeaseEndpointTests : IAsyncLifetime
{
    private readonly AdminTokenAuthEndpointTests.TokenedApi _api;
    private readonly List<int> _createdLibraries = [];

    public WorkerLeaseEndpointTests(AdminTokenAuthEndpointTests.TokenedApi api) => _api = api;

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Removes the libraries these tests create. The host fixture is shared across the collection,
    /// so rows left behind here change what other tests in it see — adding this class initially
    /// broke two unrelated setup and calibration tests that count jobs. Deleting the library
    /// cascades to its media files and their jobs.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (_createdLibraries.Count == 0) return;

        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();

        // Leases restrict deletion of their worker but cascade from the job, so clear them first.
        var jobIds = await db.Jobs
            .Where(job => job.LibraryId != null && _createdLibraries.Contains(job.LibraryId.Value))
            .Select(job => job.Id)
            .ToListAsync();
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
        using var doc = JsonDocument.Parse(current.GetRawText());
        var payload = new Dictionary<string, object?>();
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            payload[property.Name] = JsonSerializer.Deserialize<object?>(property.Value.GetRawText());
        }
        payload["remoteWorkersEnabled"] = true;
        (await admin.PutAsJsonAsync("/api/settings", payload)).EnsureSuccessStatusCode();
    }

    /// <summary>Pairs a worker that can actually satisfy a job, and returns its credential.</summary>
    private async Task<HttpClient> PairCapableWorker(string name, int concurrency = 1)
    {
        var admin = Admin();
        var issued = await admin.PostAsync("/api/workers/pairing-code", null);
        issued.EnsureSuccessStatusCode();
        var pin = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

        var paired = await _api.CreateClient().PostAsJsonAsync("/api/workers/pair", new
        {
            code = pin,
            name,
            operatingSystem = "linux",
            architecture = "x64",
            protocolMinimum = 1,
            protocolMaximum = 1,
            videoEncoders = new[] { "libx265" },
            hardwareDecoders = Array.Empty<string>(),
            vmaf = "Cpu",
            freeScratchBytes = 500L * 1024 * 1024 * 1024,
            maxConcurrency = concurrency,
        });
        paired.EnsureSuccessStatusCode();
        var credential = (await paired.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("credential").GetString()!;

        var worker = _api.CreateClient();
        worker.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return worker;
    }

    /// <summary>Puts one queued job in front of the workers and returns its id.</summary>
    private async Task<int> QueueAJob()
    {
        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();

        var library = new Library { Name = "Leases", Path = Path.Combine(_api.LibraryDirectory, Guid.NewGuid().ToString("N")) };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        _createdLibraries.Add(library.Id);

        var file = new MediaFile
        {
            LibraryId = library.Id,
            Path = Path.Combine(library.Path, "film.mkv"),
            RelativePath = "film.mkv",
            SizeBytes = 8L * 1024 * 1024 * 1024,
        };
        db.MediaFiles.Add(file);
        await db.SaveChangesAsync();

        var job = new Job
        {
            MediaFileId = file.Id,
            LibraryId = library.Id,
            Status = JobStatus.Queued,
            Type = JobType.Normal,
            VideoEncoder = "libx265",
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private async Task<JobStatus> StatusOf(int jobId)
    {
        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        return (await db.Jobs.FindAsync(jobId))!.Status;
    }

    [Fact]
    public async Task A_claimed_job_stops_being_queued_so_this_machine_cannot_run_it_too()
    {
        await EnableRemoteWorkers();
        var worker = await PairCapableWorker("Claimer");
        var jobId = await QueueAJob();

        using var claim = await worker.PostAsJsonAsync("/api/workers/claim", new { });
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        var assignment = await claim.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(jobId, assignment.GetProperty("jobId").GetInt32());
        // The whole point: the local dispatcher selects on Queued, so leaving that status is what
        // stops two encoders working the same original.
        Assert.Equal(JobStatus.Leased, await StatusOf(jobId));
    }

    [Fact]
    public async Task A_second_worker_is_not_offered_a_job_someone_already_holds()
    {
        await EnableRemoteWorkers();
        var first = await PairCapableWorker("First");
        var second = await PairCapableWorker("Second");
        await QueueAJob();

        using var firstClaim = await first.PostAsJsonAsync("/api/workers/claim", new { });
        Assert.Equal(HttpStatusCode.OK, firstClaim.StatusCode);

        using var secondClaim = await second.PostAsJsonAsync("/api/workers/claim", new { });
        Assert.Equal(HttpStatusCode.NoContent, secondClaim.StatusCode);
    }

    [Fact]
    public async Task Releasing_a_claim_puts_the_job_back_in_the_queue()
    {
        await EnableRemoteWorkers();
        var worker = await PairCapableWorker("Releaser");
        var jobId = await QueueAJob();

        var assignment = await (await worker.PostAsJsonAsync("/api/workers/claim", new { }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var leaseId = assignment.GetProperty("leaseId").GetString()!;

        using var released = await worker.PostAsJsonAsync($"/api/workers/leases/{leaseId}/release", new { });
        Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);

        // Back on the queue, runnable here or by another worker. Giving a job up must never strand
        // it.
        Assert.Equal(JobStatus.Queued, await StatusOf(jobId));
    }

    [Fact]
    public async Task A_worker_cannot_touch_a_lease_it_does_not_hold()
    {
        await EnableRemoteWorkers();
        var holder = await PairCapableWorker("Holder");
        var intruder = await PairCapableWorker("Intruder");
        await QueueAJob();

        var assignment = await (await holder.PostAsJsonAsync("/api/workers/claim", new { }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var leaseId = assignment.GetProperty("leaseId").GetString()!;

        using var stolenRenew = await intruder.PostAsJsonAsync($"/api/workers/leases/{leaseId}/renew", new { });
        using var stolenRelease = await intruder.PostAsJsonAsync($"/api/workers/leases/{leaseId}/release", new { });

        Assert.Equal(HttpStatusCode.Forbidden, stolenRenew.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, stolenRelease.StatusCode);
    }

    [Fact]
    public async Task Renewing_extends_the_claim()
    {
        await EnableRemoteWorkers();
        var worker = await PairCapableWorker("Renewer");
        await QueueAJob();

        var assignment = await (await worker.PostAsJsonAsync("/api/workers/claim", new { }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var leaseId = assignment.GetProperty("leaseId").GetString()!;
        var firstExpiry = assignment.GetProperty("expiresUtc").GetDateTimeOffset();

        await Task.Delay(1100);
        using var renewed = await worker.PostAsJsonAsync($"/api/workers/leases/{leaseId}/renew", new { });
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);

        var extended = (await renewed.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("expiresUtc").GetDateTimeOffset();
        Assert.True(extended > firstExpiry, $"expiry did not move: {firstExpiry} -> {extended}");
    }

    [Fact]
    public async Task A_worker_advertising_nothing_is_never_offered_work()
    {
        await EnableRemoteWorkers();
        await QueueAJob();

        // The pairing-only sidecar reports no encoders and zero concurrency. The matcher fails
        // closed, so it must be offered nothing rather than handed a job it cannot run.
        var admin = Admin();
        var pin = (await (await admin.PostAsync("/api/workers/pairing-code", null))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        var paired = await _api.CreateClient().PostAsJsonAsync("/api/workers/pair", new
        {
            code = pin,
            name = "Incapable",
            operatingSystem = "macos",
            architecture = "arm64",
            protocolMinimum = 1,
            protocolMaximum = 1,
            vmaf = "None",
            freeScratchBytes = 0L,
            maxConcurrency = 0,
        });
        paired.EnsureSuccessStatusCode();
        var credential = (await paired.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("credential").GetString()!;

        var incapable = _api.CreateClient();
        incapable.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);

        using var claim = await incapable.PostAsJsonAsync("/api/workers/claim", new { });
        Assert.Equal(HttpStatusCode.NoContent, claim.StatusCode);
    }

    [Fact]
    public async Task Claiming_is_refused_while_remote_workers_are_switched_off()
    {
        await EnableRemoteWorkers();
        var worker = await PairCapableWorker("Switched off");

        var admin = Admin();
        var current = await (await admin.GetAsync("/api/settings")).Content.ReadFromJsonAsync<JsonElement>();
        using (var doc = JsonDocument.Parse(current.GetRawText()))
        {
            var payload = new Dictionary<string, object?>();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                payload[property.Name] = JsonSerializer.Deserialize<object?>(property.Value.GetRawText());
            }
            payload["remoteWorkersEnabled"] = false;
            (await admin.PutAsJsonAsync("/api/settings", payload)).EnsureSuccessStatusCode();
        }

        using var claim = await worker.PostAsJsonAsync("/api/workers/claim", new { });
        Assert.Equal(HttpStatusCode.Forbidden, claim.StatusCode);

        await EnableRemoteWorkers();
    }
}
