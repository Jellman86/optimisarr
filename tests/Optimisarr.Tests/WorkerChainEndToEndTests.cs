using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Core.Domain;
using Optimisarr.Api.Workers;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Data;

namespace Optimisarr.Tests;

/// <summary>
/// One job all the way round, through the real routes, with every hash checked against the next.
///
/// <para>Each link already has tests of its own and each of them passed while the chain as a whole
/// did not work. What this adds is the join: that the hash the server serves is the hash the
/// worker's evidence names, that the hash the server computes from the bytes it received is the
/// hash that evidence was measured against, and that the evidence is then used <em>instead of</em>
/// measuring again here. The last is the whole point of the feature — the little server does the
/// arithmetic and the worker does the work — and nothing tested it end to end.</para>
///
/// <para>The negative cases sit beside the happy path deliberately. A chain is only as good as its
/// weakest link, so each test breaks exactly one link of a run that is otherwise identical.</para>
/// </summary>
[Collection(TokenedApiCollection.Name)]
public sealed class WorkerChainEndToEndTests : IAsyncLifetime
{
    private readonly AdminTokenAuthEndpointTests.TokenedApi _api;
    private readonly List<int> _createdLibraries = [];

    private static readonly byte[] SourceBytes =
        Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray();

    private static readonly byte[] CandidateBytes =
        Enumerable.Range(0, 3072).Select(i => (byte)(i % 199)).ToArray();

    /// <summary>A libvmaf log good enough to pass the library's gate, as a worker would return it.</summary>
    private const string PassingLog = """
        { "pooled_metrics": { "vmaf": { "min": 92.0, "max": 99.0, "mean": 96.0, "harmonic_mean": 95.5 } },
          "frames": [] }
        """;

    public WorkerChainEndToEndTests(AdminTokenAuthEndpointTests.TokenedApi api) => _api = api;

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
        using var document = JsonDocument.Parse(current.GetRawText());
        var payload = new Dictionary<string, object?>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            payload[property.Name] = JsonSerializer.Deserialize<object?>(property.Value.GetRawText());
        }

        payload["remoteWorkersEnabled"] = true;
        // This fixture pairs protocol-1 workers. Do not depend on another test changing the
        // fresh-install sidecar-only default in the shared host before this class runs.
        payload["workerVerificationRequired"] = false;
        (await admin.PutAsJsonAsync("/api/settings", payload)).EnsureSuccessStatusCode();
    }

    private async Task<HttpClient> PairWorker(string name)
    {
        var admin = Admin();
        var pin = (await (await admin.PostAsync("/api/workers/pairing-code", null))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        var paired = await _api.CreateClient().PostAsJsonAsync("/api/workers/pair", new
        {
            code = pin, name, operatingSystem = "windows", architecture = "x64",
            protocolMinimum = 1, protocolMaximum = 1,
            videoEncoders = new[] { "libx265" }, audioEncoders = new[] { "aac" },
            hardwareDecoders = Array.Empty<string>(),
            vmaf = "Cpu", freeScratchBytes = 500L * 1024 * 1024 * 1024, maxConcurrency = 1,
        });
        paired.EnsureSuccessStatusCode();
        var credential = (await paired.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("credential").GetString()!;
        var client = _api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return client;
    }

    private async Task<int> QueueAJob()
    {
        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var library = new Library
        {
            Name = "Chain",
            Path = Path.Combine(_api.LibraryDirectory, Guid.NewGuid().ToString("N")),
            MediaType = MediaType.Tv,
            // The gate on, because the whole question is whether its evidence is believed.
            VmafQualityGateEnabled = true,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        _createdLibraries.Add(library.Id);

        Directory.CreateDirectory(library.Path);
        var sourcePath = Path.Combine(library.Path, "episode.mkv");
        await File.WriteAllBytesAsync(sourcePath, SourceBytes);

        var file = new MediaFile
        {
            LibraryId = library.Id,
            Path = sourcePath,
            RelativePath = "episode.mkv",
            SizeBytes = SourceBytes.Length,
            MediaKind = MediaKind.Video,
            VideoCodec = "h264",
            Width = 1920,
            Height = 1080,
            DurationSeconds = 100,
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

    /// <summary>Claim, then fetch the source exactly as a sidecar does — which is what records its hash.</summary>
    private static async Task<(string LeaseId, string SourceHash, JsonElement Assignment)> ClaimAndFetch(
        HttpClient worker)
    {
        using var claim = await worker.PostAsJsonAsync("/api/workers/claim", new { });
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        var assignment = await claim.Content.ReadFromJsonAsync<JsonElement>();
        var leaseId = assignment.GetProperty("leaseId").GetString()!;

        using var source = await worker.GetAsync($"/api/workers/leases/{leaseId}/source");
        source.EnsureSuccessStatusCode();
        var served = await source.Content.ReadAsByteArrayAsync();
        var declared = source.Headers.GetValues("X-Optimisarr-Source-Sha256").Single();

        // What every sidecar does before it encodes a frame: hash what actually arrived and hold it
        // against what the server said it was sending.
        Assert.Equal(declared, Sha256(served));
        return (leaseId, declared, assignment);
    }

    private static async Task<HttpResponseMessage> ReportEvidence(
        HttpClient worker, string leaseId, string sourceHash, string candidateHash) =>
        await worker.PostAsJsonAsync($"/api/workers/leases/{leaseId}/quality", new
        {
            sourceSha256 = sourceHash,
            candidateSha256 = candidateHash,
            logs = new[] { PassingLog },
        });

    private static async Task<HttpResponseMessage> Deliver(
        HttpClient worker, string leaseId, byte[] body, string sourceHash, string candidateHash)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workers/leases/{leaseId}/result")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add("X-Optimisarr-Source-Sha256", sourceHash);
        request.Headers.Add("X-Optimisarr-Candidate-Sha256", candidateHash);
        return await worker.SendAsync(request);
    }

    [Fact]
    public async Task A_worker_claim_freezes_the_library_saving_target_with_its_verification_plan()
    {
        await EnableRemoteWorkers();
        var worker = await PairWorker("Chain-saving-target");
        var jobId = await QueueAJob();

        using (var scope = _api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var libraryId = await db.Jobs.Where(job => job.Id == jobId)
                .Select(job => job.LibraryId).SingleAsync();
            var library = await db.Libraries.SingleAsync(row => row.Id == libraryId);
            library.MinimumSizeSavingPercent = 10;
            library.MaximumSizeSavingPercent = 65;
            await db.SaveChangesAsync();
        }

        var (leaseId, _, assignment) = await ClaimAndFetch(worker);
        Assert.Equal(7372, assignment.GetProperty("maxCandidateBytes").GetInt64());
        Assert.Equal(2868, assignment.GetProperty("minCandidateBytes").GetInt64());

        using var changedScope = _api.Services.CreateScope();
        var changedDb = changedScope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var changedLibraryId = await changedDb.Jobs.Where(job => job.Id == jobId)
            .Select(job => job.LibraryId).SingleAsync();
        var changedLibrary = await changedDb.Libraries.SingleAsync(row => row.Id == changedLibraryId);
        changedLibrary.MinimumSizeSavingPercent = 20;
        changedLibrary.MaximumSizeSavingPercent = 50;
        await changedDb.SaveChangesAsync();

        var lease = await changedDb.JobLeases.AsNoTracking()
            .SingleAsync(row => row.Id == Guid.Parse(leaseId));
        using var frozen = JsonDocument.Parse(lease.VerificationWorkJson!);
        Assert.Equal(10, frozen.RootElement.GetProperty("verificationPolicy")
            .GetProperty("minimumSizeSavingPercent").GetDouble());
        Assert.Equal(65, frozen.RootElement.GetProperty("verificationPolicy")
            .GetProperty("maximumSizeSavingPercent").GetDouble());
        Assert.Equal(7372, lease.MaxCandidateBytes);
        Assert.Equal(2868, lease.MinCandidateBytes);
    }

    [Fact]
    public async Task A_whole_job_travels_out_and_back_with_every_hash_tied_to_the_next()
    {
        await EnableRemoteWorkers();
        var worker = await PairWorker("Chain");
        var jobId = await QueueAJob();

        var (leaseId, sourceHash, _) = await ClaimAndFetch(worker);
        var candidateHash = Sha256(CandidateBytes);

        // Measured on the worker, against the source it verified and the candidate it produced.
        using var evidence = await ReportEvidence(worker, leaseId, sourceHash, candidateHash);
        Assert.Equal(HttpStatusCode.OK, evidence.StatusCode);
        var accepted = await evidence.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(95.5, accepted.GetProperty("vmafHarmonicMean").GetDouble(), 3);

        using var delivery = await Deliver(worker, leaseId, CandidateBytes, sourceHash, candidateHash);
        // Accepted, not OK: the candidate is stored and queued for verification, which has not run.
        Assert.Equal(HttpStatusCode.Accepted, delivery.StatusCode);

        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var job = await db.Jobs.AsNoTracking().SingleAsync(row => row.Id == jobId);
        var lease = await db.JobLeases.AsNoTracking().SingleAsync(row => row.JobId == jobId);

        // The four hashes, and the three joins between them. This is the chain.
        Assert.Equal(Sha256(SourceBytes), job.SourceSha256);
        Assert.Equal(job.SourceSha256, lease.QualitySourceSha256);
        Assert.Equal(candidateHash, lease.DeliveredSha256);
        Assert.Equal(lease.DeliveredSha256, lease.QualityCandidateSha256);

        // And the candidate goes to verification rather than anywhere near the original.
        Assert.Equal(JobStatus.AwaitingVerification, job.Status);
        Assert.NotNull(job.WorkOutputPath);
        Assert.True(File.Exists(job.WorkOutputPath));
        Assert.Equal(CandidateBytes, await File.ReadAllBytesAsync(job.WorkOutputPath!));

        // The point of it all: this server may use that measurement instead of taking its own.
        var policy = VerificationPolicy.Default with
        {
            QualityGateEnabled = true,
            MinimumVmafHarmonicMean = 85,
            MinimumVmafMin = 70,
        };
        var resolved = DeliveredQualityEvidence.Resolve(lease, job.SourceSha256, policy);
        Assert.True(resolved.WasAsked);
        Assert.Empty(resolved.Objections);
        Assert.NotNull(resolved.Accepted);
        Assert.Equal(95.5, resolved.Accepted!.Result.Scores!.VmafHarmonicMean!.Value, 3);
    }

    [Fact]
    public async Task Evidence_for_bytes_other_than_the_ones_that_arrived_is_not_used()
    {
        // The link that makes the rest worth anything. A worker may measure whatever it likes; the
        // measurement counts only if it names the file the server is actually holding.
        await EnableRemoteWorkers();
        var worker = await PairWorker("Chain-swap");
        var jobId = await QueueAJob();

        var (leaseId, sourceHash, _) = await ClaimAndFetch(worker);

        // Evidence about one file, delivery of another. Both are individually well formed.
        using var evidence = await ReportEvidence(worker, leaseId, sourceHash, Sha256([1, 2, 3]));
        Assert.Equal(HttpStatusCode.OK, evidence.StatusCode);

        using var delivery = await Deliver(
            worker, leaseId, CandidateBytes, sourceHash, Sha256(CandidateBytes));
        Assert.Equal(HttpStatusCode.Accepted, delivery.StatusCode);

        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var job = await db.Jobs.AsNoTracking().SingleAsync(row => row.Id == jobId);
        var lease = await db.JobLeases.AsNoTracking().SingleAsync(row => row.JobId == jobId);

        var resolved = DeliveredQualityEvidence.Resolve(
            lease, job.SourceSha256, VerificationPolicy.Default with { QualityGateEnabled = true });

        Assert.Null(resolved.Accepted);
        Assert.NotEmpty(resolved.Objections);
        // Refused, not silently ignored: this server measures it itself instead.
        Assert.True(resolved.WasAsked);
    }

    [Fact]
    public async Task A_candidate_whose_bytes_do_not_match_its_declared_hash_never_reaches_a_job()
    {
        await EnableRemoteWorkers();
        var worker = await PairWorker("Chain-corrupt");
        var jobId = await QueueAJob();

        var (leaseId, sourceHash, _) = await ClaimAndFetch(worker);

        // A truncated or corrupted upload, described as the whole thing.
        using var delivery = await Deliver(
            worker, leaseId, CandidateBytes[..1024], sourceHash, Sha256(CandidateBytes));

        Assert.Equal(HttpStatusCode.Conflict, delivery.StatusCode);

        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var job = await db.Jobs.AsNoTracking().SingleAsync(row => row.Id == jobId);

        Assert.Null(job.WorkOutputPath);
        Assert.NotEqual(JobStatus.AwaitingVerification, job.Status);
    }

    [Fact]
    public async Task A_candidate_encoded_from_a_source_this_job_never_had_is_refused()
    {
        await EnableRemoteWorkers();
        var worker = await PairWorker("Chain-wrong-source");
        var jobId = await QueueAJob();

        var (leaseId, _, _) = await ClaimAndFetch(worker);

        using var delivery = await Deliver(
            worker, leaseId, CandidateBytes, Sha256([9, 9, 9]), Sha256(CandidateBytes));

        Assert.Equal(HttpStatusCode.Conflict, delivery.StatusCode);

        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var job = await db.Jobs.AsNoTracking().SingleAsync(row => row.Id == jobId);
        Assert.Null(job.WorkOutputPath);
    }
}
