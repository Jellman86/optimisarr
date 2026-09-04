using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Api.Workers;
using Optimisarr.Data;

namespace Optimisarr.Tests;

/// <summary>
/// Accepting a file produced on someone else's machine is the point in this feature where media can
/// actually be lost, so these tests lead with the ways it goes wrong rather than the happy path: a
/// candidate encoded from a different source, a result arriving after the claim lapsed, a truncated
/// upload, and a second result for one lease.
///
/// Nothing here may touch an original. A returned candidate lands in the work directory exactly as
/// a local transcode's output does, and goes no further until verification has run.
/// </summary>
[Collection(TokenedApiCollection.Name)]
public sealed class WorkerResultUploadTests : IAsyncLifetime
{
    private readonly AdminTokenAuthEndpointTests.TokenedApi _api;
    private readonly List<int> _createdLibraries = [];

    private static readonly byte[] SourceBytes =
        Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();

    private static readonly byte[] CandidateBytes =
        Enumerable.Range(0, 2048).Select(i => (byte)(i % 199)).ToArray();

    public WorkerResultUploadTests(AdminTokenAuthEndpointTests.TokenedApi api) => _api = api;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_createdLibraries.Count == 0) return;
        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var jobIds = await db.Jobs
            .Where(job => job.LibraryId != null && _createdLibraries.Contains(job.LibraryId.Value))
            .Select(job => job.Id).ToListAsync();
        db.JobLeases.RemoveRange(db.JobLeases.Where(l => jobIds.Contains(l.JobId)));
        await db.SaveChangesAsync();
        db.Libraries.RemoveRange(db.Libraries.Where(l => _createdLibraries.Contains(l.Id)));
        await db.SaveChangesAsync();
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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
        foreach (var p in doc.RootElement.EnumerateObject())
            payload[p.Name] = JsonSerializer.Deserialize<object?>(p.Value.GetRawText());
        payload["remoteWorkersEnabled"] = true;
        (await admin.PutAsJsonAsync("/api/settings", payload)).EnsureSuccessStatusCode();
    }

    private async Task<HttpClient> PairWorker(string name)
    {
        var admin = Admin();
        var pin = (await (await admin.PostAsync("/api/workers/pairing-code", null))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        var paired = await _api.CreateClient().PostAsJsonAsync("/api/workers/pair", new
        {
            code = pin, name, operatingSystem = "linux", architecture = "x64",
            protocolMinimum = 1, protocolMaximum = 1,
            videoEncoders = new[] { "libx265" }, hardwareDecoders = Array.Empty<string>(),
            vmaf = "Cpu", freeScratchBytes = 500L * 1024 * 1024 * 1024, maxConcurrency = 1,
        });
        paired.EnsureSuccessStatusCode();
        var credential = (await paired.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("credential").GetString()!;
        var client = _api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return client;
    }

    private async Task QueueAJob()
    {
        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var library = new Library { Name = "Results", Path = Path.Combine(_api.LibraryDirectory, Guid.NewGuid().ToString("N")) };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        _createdLibraries.Add(library.Id);

        Directory.CreateDirectory(library.Path);
        var sourcePath = Path.Combine(library.Path, "film.mkv");
        await File.WriteAllBytesAsync(sourcePath, SourceBytes);

        var file = new MediaFile
        {
            LibraryId = library.Id, Path = sourcePath, RelativePath = "film.mkv",
            SizeBytes = SourceBytes.Length,
        };
        db.MediaFiles.Add(file);
        await db.SaveChangesAsync();

        db.Jobs.Add(new Job
        {
            MediaFileId = file.Id, LibraryId = library.Id,
            Status = JobStatus.Queued, Type = JobType.Normal, VideoEncoder = "libx265",
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Claims a job and fetches its source, which is what records the source hash.</summary>
    private async Task<(string LeaseId, string SourceHash)> ClaimAndFetch(HttpClient worker)
    {
        var assignment = await (await worker.PostAsJsonAsync("/api/workers/claim", new { }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var leaseId = assignment.GetProperty("leaseId").GetString()!;

        using var source = await worker.GetAsync($"/api/workers/leases/{leaseId}/source");
        source.EnsureSuccessStatusCode();
        var hash = source.Headers.GetValues("X-Optimisarr-Source-Sha256").Single();
        return (leaseId, hash);
    }

    private static HttpRequestMessage Upload(string leaseId, byte[] body, string sourceHash, string? candidateHash = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workers/leases/{leaseId}/result")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add("X-Optimisarr-Source-Sha256", sourceHash);
        request.Headers.Add("X-Optimisarr-Candidate-Sha256", candidateHash ?? Sha256(body));
        return request;
    }

    [Fact]
    public async Task A_candidate_encoded_from_a_different_source_is_refused()
    {
        // The case the source hash exists for. A worker that fetched one file and returns a
        // candidate claiming a different origin has produced something that is not evidence about
        // this job at all, whatever its quality.
        await EnableRemoteWorkers();
        var worker = await PairWorker("Wrong source");
        await QueueAJob();
        var (leaseId, _) = await ClaimAndFetch(worker);

        var wrongSource = Sha256(Encoding.UTF8.GetBytes("a completely different original"));
        using var response = await worker.SendAsync(Upload(leaseId, CandidateBytes, wrongSource));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_truncated_upload_is_refused_rather_than_stored()
    {
        // A transfer that dies part-way leaves a shorter file whose hash cannot match. Accepting it
        // would mean verifying a partial encode and potentially replacing an original with it.
        await EnableRemoteWorkers();
        var worker = await PairWorker("Truncated");
        await QueueAJob();
        var (leaseId, sourceHash) = await ClaimAndFetch(worker);

        var truncated = CandidateBytes.Take(500).ToArray();
        // Claims the hash of the whole file while sending only part of it.
        using var response = await worker.SendAsync(
            Upload(leaseId, truncated, sourceHash, candidateHash: Sha256(CandidateBytes)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_result_arriving_after_the_claim_lapsed_is_refused()
    {
        // The late-result case: the worker went away, the job moved on, and it comes back with a
        // candidate. It must not be accepted through a claim it no longer holds.
        await EnableRemoteWorkers();
        var worker = await PairWorker("Late");
        await QueueAJob();
        var (leaseId, sourceHash) = await ClaimAndFetch(worker);

        (await worker.PostAsJsonAsync($"/api/workers/leases/{leaseId}/release", new { }))
            .EnsureSuccessStatusCode();

        using var response = await worker.SendAsync(Upload(leaseId, CandidateBytes, sourceHash));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_worker_cannot_deliver_a_result_for_someone_elses_lease()
    {
        await EnableRemoteWorkers();
        var holder = await PairWorker("Result holder");
        var intruder = await PairWorker("Result intruder");
        await QueueAJob();
        var (leaseId, sourceHash) = await ClaimAndFetch(holder);

        using var stolen = await intruder.SendAsync(Upload(leaseId, CandidateBytes, sourceHash));
        Assert.Equal(HttpStatusCode.Forbidden, stolen.StatusCode);

        using var anonymous = await _api.CreateClient()
            .SendAsync(Upload(leaseId, CandidateBytes, sourceHash));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task A_second_result_for_one_lease_is_refused()
    {
        // Duplicate delivery, which the roadmap calls out explicitly. The first result completes
        // the lease, so the second has no live claim to arrive through.
        await EnableRemoteWorkers();
        var worker = await PairWorker("Duplicate");
        await QueueAJob();
        var (leaseId, sourceHash) = await ClaimAndFetch(worker);

        using var first = await worker.SendAsync(Upload(leaseId, CandidateBytes, sourceHash));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        using var second = await worker.SendAsync(Upload(leaseId, CandidateBytes, sourceHash));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task An_accepted_candidate_lands_in_the_work_directory_and_never_over_the_original()
    {
        await EnableRemoteWorkers();
        var worker = await PairWorker("Accepted");
        await QueueAJob();
        var (leaseId, sourceHash) = await ClaimAndFetch(worker);

        using var response = await worker.SendAsync(Upload(leaseId, CandidateBytes, sourceHash));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var job = await db.Jobs.Include(j => j.MediaFile)
            .FirstAsync(j => j.LibraryId != null && _createdLibraries.Contains(j.LibraryId.Value));

        Assert.NotNull(job.WorkOutputPath);
        Assert.True(File.Exists(job.WorkOutputPath));
        Assert.Equal(CandidateBytes, await File.ReadAllBytesAsync(job.WorkOutputPath!));

        // Waiting, not Verifying: the dispatcher picks it up in its turn, and restart recovery
        // leaves it alone rather than treating it as an interrupted local encode.
        Assert.Equal(JobStatus.AwaitingVerification, job.Status);
        Assert.True(RemoteCandidate.IsDelivered(job.WorkOutputPath));

        // The original must be exactly as it was. A returned candidate is a proposal, not a
        // replacement, and nothing about delivering one may touch the source.
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(job.MediaFile!.Path));
    }

    [Fact]
    public async Task A_candidate_for_a_job_the_operator_cancelled_is_refused()
    {
        // The lease is still live, but the job is not: someone chose to stop it while the worker
        // was encoding. Accepting the candidate would quietly revive that work.
        await EnableRemoteWorkers();
        var worker = await PairWorker("Late");
        await QueueAJob();
        var (leaseId, sourceHash) = await ClaimAndFetch(worker);

        int jobId;
        using (var scope = _api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var job = await db.Jobs.FirstAsync(j => j.LibraryId != null && _createdLibraries.Contains(j.LibraryId.Value));
            job.Status = JobStatus.Cancelled;
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        using var response = await worker.SendAsync(Upload(leaseId, CandidateBytes, sourceHash));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var check = _api.Services.CreateScope();
        var after = await check.ServiceProvider.GetRequiredService<OptimisarrDbContext>().Jobs.FindAsync(jobId);
        Assert.Equal(JobStatus.Cancelled, after!.Status);
        Assert.Null(after.WorkOutputPath);
    }
}
