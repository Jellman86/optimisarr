using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
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

    /// <summary>Small but non-trivial, so hashing and range requests have something to work on.</summary>
    private static readonly byte[] SourceBytes =
        Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();

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
    private Task<HttpClient> PairCapableWorker(string name, int concurrency = 1) =>
        PairWorkerWithEncoders(name, concurrency, "libx265");

    private Task<HttpClient> PairWorkerWithEncoders(string name, params string[] encoders) =>
        PairWorkerWithEncoders(name, 1, encoders);

    private async Task<HttpClient> PairWorkerWithEncoders(string name, int concurrency, params string[] encoders)
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
            videoEncoders = encoders,
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
    private async Task<int> QueueAJob(
        string? videoEncoder = "libx265",
        VideoQualityStrategy strategy = VideoQualityStrategy.Fixed,
        RuleProfile profile = RuleProfile.ConservativeHevc,
        WorkPlacement placement = WorkPlacement.Anywhere,
        bool qualityGate = false)
    {
        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();

        var library = new Library
        {
            Name = "Leases",
            Path = Path.Combine(_api.LibraryDirectory, Guid.NewGuid().ToString("N")),
            VideoQualityStrategy = strategy,
            RuleProfile = profile,
            WorkPlacement = placement,
            VmafQualityGateEnabled = qualityGate ? true : null,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        _createdLibraries.Add(library.Id);

        Directory.CreateDirectory(library.Path);
        var sourcePath = Path.Combine(library.Path, "film.mkv");
        // Real bytes on disk: the source route streams and hashes the actual file, so a row
        // pointing at nothing would only exercise the missing-file path.
        await File.WriteAllBytesAsync(sourcePath, SourceBytes);

        var file = new MediaFile
        {
            LibraryId = library.Id,
            Path = sourcePath,
            RelativePath = "film.mkv",
            SizeBytes = SourceBytes.Length,
            // The inventory's picture facts, as a scan would have recorded them: the assignment
            // names its VMAF model from the size, and a re-encode needs a video to re-encode.
            MediaKind = MediaKind.Video,
            VideoCodec = "h264",
            Width = 1920,
            Height = 1080,
            // Known so a worker's encoded seconds can become a fraction of the whole.
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
            VideoEncoder = videoEncoder,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private async Task<int> WorkerIdNamed(string name)
    {
        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        return await db.Workers.Where(w => w.Name == name && w.RevokedAt == null)
            .OrderByDescending(w => w.Id).Select(w => w.Id).FirstAsync();
    }

    [Fact]
    public async Task A_draining_worker_is_offered_nothing_until_it_is_resumed()
    {
        // Drain is a claim refusal and nothing more. The queue is untouched, the worker keeps
        // checking in, and resuming hands it the same job it was refused a moment earlier.
        await EnableRemoteWorkers();
        var worker = await PairCapableWorker("Drainer");
        var workerId = await WorkerIdNamed("Drainer");
        var jobId = await QueueAJob();
        var admin = Admin();

        using var drained = await admin.PostAsync($"/api/workers/{workerId}/drain", null);
        Assert.Equal(HttpStatusCode.OK, drained.StatusCode);
        var drainedRow = await drained.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(JsonValueKind.Null, drainedRow.GetProperty("drainRequestedAt").ValueKind);

        using var refused = await worker.PostAsJsonAsync("/api/workers/claim", new { });
        Assert.Equal(HttpStatusCode.NoContent, refused.StatusCode);
        Assert.Equal(JobStatus.Queued, await StatusOf(jobId));

        using var resumed = await admin.DeleteAsync($"/api/workers/{workerId}/drain");
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        var resumedRow = await resumed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, resumedRow.GetProperty("drainRequestedAt").ValueKind);

        using var offered = await worker.PostAsJsonAsync("/api/workers/claim", new { });
        Assert.Equal(HttpStatusCode.OK, offered.StatusCode);
        Assert.Equal(JobStatus.Leased, await StatusOf(jobId));
    }

    [Fact]
    public async Task Draining_keeps_the_lease_a_worker_already_holds()
    {
        // The whole point of drain over revoke: work in flight finishes. The lease still renews,
        // the operator can see it is what the drain is waiting on, and the sidecar hears about
        // the drain on its next check-in rather than at the end of the job.
        await EnableRemoteWorkers();
        var worker = await PairCapableWorker("Finisher");
        var workerId = await WorkerIdNamed("Finisher");
        await QueueAJob();
        using var claim = await worker.PostAsJsonAsync("/api/workers/claim", new { });
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        var leaseId = (await claim.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("leaseId").GetString()!;

        using var drained = await Admin().PostAsync($"/api/workers/{workerId}/drain", null);
        Assert.Equal(HttpStatusCode.OK, drained.StatusCode);
        Assert.Equal(1, (await drained.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("heldLeases").GetInt32());

        using var renewed = await worker.PostAsJsonAsync($"/api/workers/leases/{leaseId}/renew", new { });
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);

        using var beat = await worker.PostAsJsonAsync("/api/workers/heartbeat", new
        {
            freeScratchBytes = 500L * 1024 * 1024 * 1024,
            maxConcurrency = 1,
        });
        Assert.Equal(HttpStatusCode.OK, beat.StatusCode);
        Assert.True((await beat.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("draining").GetBoolean());
    }

    [Fact]
    public async Task Renewing_reports_where_the_worker_is_and_moves_the_queue_bar()
    {
        // The worker only knows ffmpeg's out_time; the server owns the duration, so the fraction
        // is computed here and shown in two places from one number: the job's own progress and
        // the worker's card.
        await EnableRemoteWorkers();
        var worker = await PairCapableWorker("Reporter");
        var workerId = await WorkerIdNamed("Reporter");
        var jobId = await QueueAJob();
        using var claim = await worker.PostAsJsonAsync("/api/workers/claim", new { });
        var leaseId = (await claim.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("leaseId").GetString()!;

        using var renewed = await worker.PostAsJsonAsync(
            $"/api/workers/leases/{leaseId}/renew", new { stage = "Encoding", encodedSeconds = 50.0 });
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);

        using var listed = await Admin().GetAsync("/api/workers");
        var row = (await listed.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().Single(w => w.GetProperty("id").GetInt32() == workerId);
        var active = Assert.Single(row.GetProperty("activeJobs").EnumerateArray());
        Assert.Equal(jobId, active.GetProperty("jobId").GetInt32());
        Assert.Equal("film.mkv", active.GetProperty("relativePath").GetString());
        Assert.Equal("Encoding", active.GetProperty("stage").GetString());
        Assert.Equal(0.5, active.GetProperty("progress").GetDouble(), precision: 3);
        Assert.Equal(1, row.GetProperty("heldLeases").GetInt32());
    }

    [Fact]
    public async Task The_queue_row_names_the_worker_and_its_stage()
    {
        // The same lease facts the Workers tab shows, read from the job's side: a row that says
        // "encoding on Mac Studio" rather than a bare "Encoding remotely".
        await EnableRemoteWorkers();
        var worker = await PairCapableWorker("Rowmaker");
        var jobId = await QueueAJob();
        using var claim = await worker.PostAsJsonAsync("/api/workers/claim", new { });
        var leaseId = (await claim.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("leaseId").GetString()!;
        (await worker.PostAsJsonAsync($"/api/workers/leases/{leaseId}/renew", new { stage = "Encoding", encodedSeconds = 25.0 }))
            .EnsureSuccessStatusCode();

        var row = await JobRow(jobId);

        Assert.Equal("Leased", row.GetProperty("status").GetString());
        Assert.Equal("Rowmaker", row.GetProperty("workerName").GetString());
        Assert.Equal("Encoding", row.GetProperty("remoteStage").GetString());
        Assert.Equal(0.25, row.GetProperty("progress").GetDouble(), precision: 3);
        Assert.False(row.GetProperty("waitingForWorker").GetBoolean());
    }

    [Fact]
    public async Task A_queued_job_kept_for_a_worker_says_it_is_waiting()
    {
        // The row is judged by the dispatcher's own rule, so "waiting for a worker" is never shown
        // for a job this server would start, and always shown for one it will not.
        await EnableRemoteWorkers();
        var held = await QueueAJob(placement: WorkPlacement.WorkerOnly);
        var free = await QueueAJob(placement: WorkPlacement.Anywhere);

        Assert.True((await JobRow(held)).GetProperty("waitingForWorker").GetBoolean());
        Assert.False((await JobRow(free)).GetProperty("waitingForWorker").GetBoolean());
        Assert.Equal(JsonValueKind.Null, (await JobRow(free)).GetProperty("workerName").ValueKind);
    }

    [Fact]
    public async Task A_job_that_prefers_a_worker_waits_only_while_one_is_online()
    {
        // Exercises the liveness lookup against the real database: SQLite cannot compare a
        // DateTimeOffset column, so the comparison must run in memory or the whole queue feed
        // fails the moment remote workers are on.
        await EnableRemoteWorkers();
        // The host is shared across this collection, so workers other tests paired are still
        // online. Draining them is the operator's own way of saying "not you", and it is what
        // the availability rule excludes, so the first assertion starts from nothing available.
        var admin = Admin();
        foreach (var row in (await (await admin.GetAsync("/api/workers")).Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray())
        {
            if (row.GetProperty("revokedAt").ValueKind == JsonValueKind.Null)
            {
                (await admin.PostAsync($"/api/workers/{row.GetProperty("id").GetInt32()}/drain", null)).EnsureSuccessStatusCode();
            }
        }

        var jobId = await QueueAJob(placement: WorkPlacement.PreferWorker);
        Assert.False((await JobRow(jobId)).GetProperty("waitingForWorker").GetBoolean());

        // Pairing checks the worker in, so from here one is online and could take the job.
        await PairCapableWorker("Preferred");
        Assert.True((await JobRow(jobId)).GetProperty("waitingForWorker").GetBoolean());
    }

    private async Task<JsonElement> JobRow(int jobId)
    {
        using var listed = await Admin().GetAsync("/api/jobs");
        listed.EnsureSuccessStatusCode();
        return (await listed.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().Single(job => job.GetProperty("id").GetInt32() == jobId);
    }

    private const string LibvmafLog = """
        {
          "frames": [ { "frameNum": 0, "metrics": { "vmaf": 96.0 } }, { "frameNum": 1, "metrics": { "vmaf": 94.0 } } ],
          "pooled_metrics": { "vmaf": { "min": 94.0, "max": 96.0, "mean": 95.0, "harmonic_mean": 94.9 } }
        }
        """;

    [Fact]
    public async Task An_assignment_carries_the_servers_own_measurement_command()
    {
        // The worker is told what to measure the way it is told what to encode: the server's own
        // command, with placeholders where this machine's paths would be. Nothing about windows,
        // models or thresholds is left for the worker to derive.
        await EnableRemoteWorkers();
        var worker = await PairCapableWorker("Measurer");
        await QueueAJob(profile: RuleProfile.ConservativeHevc, qualityGate: true);

        using var claim = await worker.PostAsJsonAsync("/api/workers/claim", new { });
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        var quality = (await claim.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("quality");

        Assert.True(quality.GetProperty("measure").GetBoolean());
        Assert.Equal("Full file", quality.GetProperty("sampling").GetString());
        var command = Assert.Single(quality.GetProperty("commands").EnumerateArray()).EnumerateArray().Select(a => a.GetString()!).ToList();
        Assert.Contains("{{distorted}}", command);
        Assert.Contains("{{reference}}", command);
        Assert.Contains(command, arg => arg.Contains("log_path={{log}}") && arg.Contains("model=version=vmaf_v0.6.1"));
        Assert.Equal("-", command[^1]);
    }

    [Fact]
    public async Task A_job_without_a_quality_gate_asks_the_worker_to_measure_nothing()
    {
        await EnableRemoteWorkers();
        var worker = await PairCapableWorker("Measurer");
        await QueueAJob();

        using var claim = await worker.PostAsJsonAsync("/api/workers/claim", new { });
        var quality = (await claim.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("quality");

        Assert.False(quality.GetProperty("measure").GetBoolean());
        Assert.Empty(quality.GetProperty("commands").EnumerateArray());
    }

    [Fact]
    public async Task Returned_libvmaf_logs_are_parsed_here_and_bound_to_both_hashes()
    {
        await EnableRemoteWorkers();
        var worker = await PairCapableWorker("Measurer");
        var jobId = await QueueAJob(qualityGate: true);
        using var claim = await worker.PostAsJsonAsync("/api/workers/claim", new { });
        var leaseId = (await claim.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("leaseId").GetString()!;
        var sourceHash = await SourceHashOf(jobId, worker, leaseId);

        using var reported = await worker.PostAsJsonAsync($"/api/workers/leases/{leaseId}/quality", new
        {
            sourceSha256 = sourceHash,
            candidateSha256 = "cafe",
            logs = new[] { LibvmafLog },
        });

        Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        var pooled = await reported.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(94.9, pooled.GetProperty("vmafHarmonicMean").GetDouble(), precision: 3);
        Assert.Equal(2, pooled.GetProperty("frameCount").GetInt32());

        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var lease = await db.JobLeases.SingleAsync(l => l.Id == Guid.Parse(leaseId));
        Assert.Equal("cafe", lease.QualityCandidateSha256);
        Assert.Contains("94.9", lease.QualityScoresJson);
    }

    [Fact]
    public async Task Evidence_with_the_wrong_number_of_logs_or_an_unreadable_log_is_refused()
    {
        await EnableRemoteWorkers();
        var worker = await PairCapableWorker("Measurer");
        var jobId = await QueueAJob(qualityGate: true);
        using var claim = await worker.PostAsJsonAsync("/api/workers/claim", new { });
        var leaseId = (await claim.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("leaseId").GetString()!;
        var sourceHash = await SourceHashOf(jobId, worker, leaseId);

        using var tooMany = await worker.PostAsJsonAsync($"/api/workers/leases/{leaseId}/quality", new
        {
            sourceSha256 = sourceHash, candidateSha256 = "cafe", logs = new[] { LibvmafLog, LibvmafLog },
        });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);

        using var garbage = await worker.PostAsJsonAsync($"/api/workers/leases/{leaseId}/quality", new
        {
            sourceSha256 = sourceHash, candidateSha256 = "cafe", logs = new[] { "not json" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
    }

    [Fact]
    public async Task Evidence_measured_against_another_source_is_refused_and_written_on_the_card()
    {
        await EnableRemoteWorkers();
        var worker = await PairCapableWorker("Measurer");
        var workerId = await WorkerIdNamed("Measurer");
        var jobId = await QueueAJob(qualityGate: true);
        using var claim = await worker.PostAsJsonAsync("/api/workers/claim", new { });
        var leaseId = (await claim.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("leaseId").GetString()!;
        await SourceHashOf(jobId, worker, leaseId);

        using var refused = await worker.PostAsJsonAsync($"/api/workers/leases/{leaseId}/quality", new
        {
            sourceSha256 = "0000", candidateSha256 = "cafe", logs = new[] { LibvmafLog },
        });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        using var listed = await Admin().GetAsync("/api/workers");
        var row = (await listed.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().Single(w => w.GetProperty("id").GetInt32() == workerId);
        Assert.Contains("different source", row.GetProperty("lastProblem").GetString());
    }

    [Fact]
    public async Task Evidence_cannot_be_reported_for_a_lease_that_asked_for_none()
    {
        await EnableRemoteWorkers();
        var worker = await PairCapableWorker("Measurer");
        await QueueAJob();
        using var claim = await worker.PostAsJsonAsync("/api/workers/claim", new { });
        var leaseId = (await claim.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("leaseId").GetString()!;

        using var refused = await worker.PostAsJsonAsync($"/api/workers/leases/{leaseId}/quality", new
        {
            sourceSha256 = "0000", candidateSha256 = "cafe", logs = new[] { LibvmafLog },
        });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    /// <summary>Fetching the source is what stamps the job's source hash; returns it.</summary>
    private async Task<string> SourceHashOf(int jobId, HttpClient worker, string leaseId)
    {
        using var source = await worker.GetAsync($"/api/workers/leases/{leaseId}/source");
        source.EnsureSuccessStatusCode();
        return source.Headers.GetValues("X-Optimisarr-Source-Sha256").Single();
    }

    [Fact]
    public async Task Renewing_with_an_unknown_stage_is_refused()
    {
        await EnableRemoteWorkers();
        var worker = await PairCapableWorker("Claimer");
        await QueueAJob();
        using var claim = await worker.PostAsJsonAsync("/api/workers/claim", new { });
        var leaseId = (await claim.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("leaseId").GetString()!;

        using var renewed = await worker.PostAsJsonAsync(
            $"/api/workers/leases/{leaseId}/renew", new { stage = "Teleporting" });

        Assert.Equal(HttpStatusCode.BadRequest, renewed.StatusCode);
    }

    [Fact]
    public async Task Renewing_without_a_body_still_extends_the_claim()
    {
        // An older sidecar renews with nothing but the lease id, and must keep working.
        await EnableRemoteWorkers();
        var worker = await PairCapableWorker("Claimer");
        await QueueAJob();
        using var claim = await worker.PostAsJsonAsync("/api/workers/claim", new { });
        var leaseId = (await claim.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("leaseId").GetString()!;

        using var renewed = await worker.PostAsync($"/api/workers/leases/{leaseId}/renew", null);

        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
    }

    [Fact]
    public async Task A_lapsed_lease_is_written_on_the_workers_card()
    {
        // The worker that went quiet never hears its lease lapsed; the operator does, on the
        // worker's own card, when the next claim from anyone reclaims it.
        await EnableRemoteWorkers();
        var quiet = await PairCapableWorker("Quiet");
        var quietId = await WorkerIdNamed("Quiet");
        var jobId = await QueueAJob();
        using var claim = await quiet.PostAsJsonAsync("/api/workers/claim", new { });
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);

        using (var scope = _api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var lease = await db.JobLeases.SingleAsync(l => l.JobId == jobId && l.State == Optimisarr.Core.Workers.LeaseState.Held);
            lease.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var other = await PairCapableWorker("Other");
        using var reclaim = await other.PostAsJsonAsync("/api/workers/claim", new { });
        Assert.Equal(HttpStatusCode.OK, reclaim.StatusCode);

        using var listed = await Admin().GetAsync("/api/workers");
        var row = (await listed.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().Single(w => w.GetProperty("id").GetInt32() == quietId);
        Assert.Contains("lapsed", row.GetProperty("lastProblem").GetString());
        Assert.Contains("film.mkv", row.GetProperty("lastProblem").GetString());
        Assert.Empty(row.GetProperty("activeJobs").EnumerateArray());
    }

    [Fact]
    public async Task A_revoked_worker_cannot_be_resumed()
    {
        await EnableRemoteWorkers();
        await PairCapableWorker("Gone");
        var workerId = await WorkerIdNamed("Gone");
        var admin = Admin();
        (await admin.DeleteAsync($"/api/workers/{workerId}")).EnsureSuccessStatusCode();

        using var resumed = await admin.DeleteAsync($"/api/workers/{workerId}/drain");

        Assert.Equal(HttpStatusCode.Conflict, resumed.StatusCode);
    }

    private async Task<JobStatus> StatusOf(int jobId)
    {
        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        return (await db.Jobs.FindAsync(jobId))!.Status;
    }

    [Fact]
    public async Task A_job_enqueued_the_way_the_application_enqueues_one_is_offered_with_an_executable_command()
    {
        // The application never sets VideoEncoder on a queued job; that column records what ran.
        // The encoder is resolved here, for this worker, from what it proved — and the assignment
        // carries the exact command this machine would have run, with the worker's paths as tokens.
        await EnableRemoteWorkers();
        var worker = await PairCapableWorker("Claimer");
        var jobId = await QueueAJob(videoEncoder: null);

        using var claim = await worker.PostAsJsonAsync("/api/workers/claim", new { });

        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        var assignment = await claim.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(jobId, assignment.GetProperty("jobId").GetInt32());
        Assert.Equal("libx265", assignment.GetProperty("videoEncoder").GetString());

        var arguments = assignment.GetProperty("arguments").EnumerateArray()
            .Select(element => element.GetString()!).ToList();
        Assert.Equal("{{input}}", arguments[arguments.IndexOf("-i") + 1]);
        // ConservativeHevc targets MP4, and the extension is part of the contract: it decides the
        // muxer and the subtitle codec, so it travels with the placeholder rather than being guessed.
        Assert.Equal("{{output}}.mp4", arguments[^1]);
        Assert.Equal("mp4", assignment.GetProperty("outputExtension").GetString());
        Assert.Equal("libx265", arguments[arguments.IndexOf("-c:v:0") + 1]);
        Assert.Contains("-crf", arguments);
        // No path on this machine reaches a worker, and no thread limit either — that is ours.
        Assert.DoesNotContain(arguments, argument => argument.StartsWith('/'));
        Assert.DoesNotContain("-threads", arguments);
        Assert.False(assignment.TryGetProperty("sourcePath", out _));

        var quality = assignment.GetProperty("quality");
        Assert.Equal("vmaf_v0.6.1", quality.GetProperty("model").GetString());
        Assert.True(quality.GetProperty("minimumHarmonicMean").GetDouble() > 0);
    }

    [Fact]
    public async Task A_job_from_an_adaptive_library_stays_local_until_its_quality_has_been_chosen()
    {
        // Adaptive selection runs sample encodes on this machine's encoder; a quality chosen for
        // one encoder means nothing on another. Offering the job at the fixed quality instead
        // would silently change what the library asked for, so it is not offered at all.
        await EnableRemoteWorkers();
        var worker = await PairCapableWorker("Claimer");
        await QueueAJob(videoEncoder: null, strategy: VideoQualityStrategy.AdaptiveVmaf);

        using var claim = await worker.PostAsJsonAsync("/api/workers/claim", new { });

        Assert.Equal(HttpStatusCode.NoContent, claim.StatusCode);
    }

    [Fact]
    public async Task A_library_kept_on_this_server_is_never_offered_to_a_worker()
    {
        // The operator said this library's encodes stay here — perhaps this machine's encoder is
        // the one they trust for it, perhaps the files are slow to send. A capable worker asking
        // for work gets the next job that is allowed to travel, or nothing.
        await EnableRemoteWorkers();
        var worker = await PairCapableWorker("Claimer");
        await QueueAJob(placement: WorkPlacement.LocalOnly);

        using var claim = await worker.PostAsJsonAsync("/api/workers/claim", new { });

        Assert.Equal(HttpStatusCode.NoContent, claim.StatusCode);
    }

    [Fact]
    public async Task A_worker_only_job_is_offered_like_any_other()
    {
        await EnableRemoteWorkers();
        var worker = await PairCapableWorker("Claimer");
        var jobId = await QueueAJob(placement: WorkPlacement.WorkerOnly);

        using var claim = await worker.PostAsJsonAsync("/api/workers/claim", new { });

        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        Assert.Equal(JobStatus.Leased, await StatusOf(jobId));
    }

    [Fact]
    public async Task A_remux_only_job_is_not_offered_to_a_remote_worker()
    {
        await EnableRemoteWorkers();
        var worker = await PairCapableWorker("Claimer");
        await QueueAJob(videoEncoder: null, profile: RuleProfile.RemuxCleanup);

        using var claim = await worker.PostAsJsonAsync("/api/workers/claim", new { });

        Assert.Equal(HttpStatusCode.NoContent, claim.StatusCode);
    }

    [Fact]
    public async Task A_worker_that_proves_a_hardware_encoder_is_handed_a_command_for_it()
    {
        // A Mac proving VideoToolbox gets VideoToolbox's own quality control, not a CRF it would
        // reject — the same builder that serves local hardware encoders resolved it for the worker.
        await EnableRemoteWorkers();
        var worker = await PairWorkerWithEncoders("MacMini", "libx265", "hevc_videotoolbox");
        await QueueAJob(videoEncoder: null);

        using var claim = await worker.PostAsJsonAsync("/api/workers/claim", new { });

        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        var assignment = await claim.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("hevc_videotoolbox", assignment.GetProperty("videoEncoder").GetString());
        var arguments = assignment.GetProperty("arguments").EnumerateArray()
            .Select(element => element.GetString()!).ToList();
        Assert.Contains("-q:v", arguments);
        Assert.DoesNotContain("-crf", arguments);
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
    public async Task The_lease_holder_can_fetch_its_source_with_a_hash_it_can_verify()
    {
        await EnableRemoteWorkers();
        var worker = await PairCapableWorker("Fetcher");
        await QueueAJob();

        var assignment = await (await worker.PostAsJsonAsync("/api/workers/claim", new { }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var leaseId = assignment.GetProperty("leaseId").GetString()!;

        using var source = await worker.GetAsync($"/api/workers/leases/{leaseId}/source");
        Assert.Equal(HttpStatusCode.OK, source.StatusCode);

        var received = await source.Content.ReadAsByteArrayAsync();
        Assert.Equal(SourceBytes, received);

        // The worker must be able to confirm what it received without a second pass, and the value
        // is what later binds a returned candidate to these exact bytes.
        var advertised = source.Headers.GetValues("X-Optimisarr-Source-Sha256").Single();
        var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(received)).ToLowerInvariant();
        Assert.Equal(actual, advertised);
    }

    [Fact]
    public async Task A_worker_cannot_fetch_a_source_for_a_lease_it_does_not_hold()
    {
        await EnableRemoteWorkers();
        var holder = await PairCapableWorker("Source holder");
        var intruder = await PairCapableWorker("Source intruder");
        await QueueAJob();

        var assignment = await (await holder.PostAsJsonAsync("/api/workers/claim", new { }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var leaseId = assignment.GetProperty("leaseId").GetString()!;

        using var stolen = await intruder.GetAsync($"/api/workers/leases/{leaseId}/source");
        Assert.Equal(HttpStatusCode.Forbidden, stolen.StatusCode);

        using var anonymous = await _api.CreateClient().GetAsync($"/api/workers/leases/{leaseId}/source");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task A_released_lease_stops_granting_access_to_the_media()
    {
        // The lease is what bounds a worker's reach into the library. Giving the job back must end
        // that reach, or a worker could keep pulling media it no longer has any claim on.
        await EnableRemoteWorkers();
        var worker = await PairCapableWorker("Handback");
        await QueueAJob();

        var assignment = await (await worker.PostAsJsonAsync("/api/workers/claim", new { }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var leaseId = assignment.GetProperty("leaseId").GetString()!;

        (await worker.PostAsJsonAsync($"/api/workers/leases/{leaseId}/release", new { }))
            .EnsureSuccessStatusCode();

        using var afterRelease = await worker.GetAsync($"/api/workers/leases/{leaseId}/source");
        Assert.Equal(HttpStatusCode.Conflict, afterRelease.StatusCode);
    }

    [Fact]
    public async Task A_dropped_transfer_can_resume_with_a_range_request()
    {
        await EnableRemoteWorkers();
        var worker = await PairCapableWorker("Resumer");
        await QueueAJob();

        var assignment = await (await worker.PostAsJsonAsync("/api/workers/claim", new { }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var leaseId = assignment.GetProperty("leaseId").GetString()!;

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/workers/leases/{leaseId}/source");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1000, null);

        using var partial = await worker.SendAsync(request);

        // Without this a worker whose connection drops part-way through a multi-gigabyte source has
        // to start again from zero.
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        var tail = await partial.Content.ReadAsByteArrayAsync();
        Assert.Equal(SourceBytes.Skip(1000).ToArray(), tail);
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
