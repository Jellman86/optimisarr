using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Tests;

[Collection(TokenedApiCollection.Name)]
public sealed class WorkerPerceptualSearchEndpointTests(AdminTokenAuthEndpointTests.TokenedApi api) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly string Source = new('a', 64), Candidate = new('b', 64), Tool = new('c', 64);
    private readonly List<int> _libraries = [], _workers = [];
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var jobs = await db.Jobs.Where(j => j.LibraryId != null && _libraries.Contains(j.LibraryId.Value)).Select(j => j.Id).ToArrayAsync();
        db.JobLeases.RemoveRange(db.JobLeases.Where(l => jobs.Contains(l.JobId)));
        await db.SaveChangesAsync();
        db.Libraries.RemoveRange(db.Libraries.Where(l => _libraries.Contains(l.Id)));
        db.Workers.RemoveRange(db.Workers.Where(w => _workers.Contains(w.Id)));
        await db.SaveChangesAsync();
    }
    private async Task<(HttpClient Client, Guid Lease, int Job)> Arrange()
    {
        using var admin = api.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        var settings = await (await admin.GetAsync("/api/settings")).Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
        var payload = settings!.ToDictionary(p => p.Key, p => (object?)p.Value);
        payload["remoteWorkersEnabled"] = true;
        (await admin.PutAsJsonAsync("/api/settings", payload)).EnsureSuccessStatusCode();
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var library = new Library { Name = "Perceptual lease fixture", Path = "/fixture-" + Guid.NewGuid(), MediaType = MediaType.Photo };
        var credential = WorkerCredential.Issue();
        var worker = new Worker { Name = "Perceptual fixture", ProtocolVersion = 11, MaxConcurrency = 1,
            CredentialFingerprint = WorkerCredential.Fingerprint(credential), LastSeenAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(library); db.Workers.Add(worker); await db.SaveChangesAsync();
        _libraries.Add(library.Id); _workers.Add(worker.Id);
        var media = new MediaFile { LibraryId = library.Id, Path = library.Path + "/original.png", RelativePath = "original.png",
            SizeBytes = 1000, MediaKind = MediaKind.Image, Width = 32, Height = 32, PixelFormat = "rgb24" };
        db.MediaFiles.Add(media); await db.SaveChangesAsync();
        var job = new Job { MediaFileId = media.Id, LibraryId = library.Id, Status = JobStatus.Leased, SourceSha256 = Source, ExecutionAttempt = 1 };
        db.Jobs.Add(job); await db.SaveChangesAsync();
        var policy = VerificationPolicy.Default with { ImagePerceptualGateEnabled = true, MinimumImagePerceptualScore = 85, ImageQualityGateEnabled = false };
        var contract = new RemotePerceptualSearchContract(Guid.NewGuid(), MediaKind.Image, "libwebp", PerceptualQualityPlan.Image("libwebp", 80),
            policy, [], SourceImage: new(32, 32, false));
        var spec = new TranscodeSpec(WorkerProtocol.InputPlaceholder, WorkerProtocol.OutputPlaceholder + ".webp", null, null, null, false,
            Kind: MediaKind.Image, ImageEncoder: "libwebp", ImageQuality: 80);
        var args = FfmpegCommandBuilder.Build(spec, optimisedMarker: "claim-version");
        var state = RemotePerceptualSearchState.Start(contract, FrozenPerceptualEncoding.Create(contract, spec, args));
        var work = new QueueDispatcher.JobWork(spec, args, null, null, false, false, null, null, null, false, null, false, media.Id,
            new(media.Path, 1000, null, 0, 0, false, false, Kind: MediaKind.Image, VideoReencoded: false), policy,
            VideoQualityStrategy.Fixed, WorkPlacement.WorkerOnly, null, false, 0, false, false);
        var lease = new JobLease { Id = Guid.NewGuid(), JobId = job.Id, WorkerId = worker.Id, ExecutionAttempt = 1,
            State = LeaseState.Held, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10), OutputExtension = "webp",
            PerceptualSearchStateJson = JsonSerializer.Serialize(state, Json), VerificationWorkJson = JsonSerializer.Serialize(work, Json) };
        db.JobLeases.Add(lease); await db.SaveChangesAsync();
        var client = api.CreateClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return (client, lease.Id, job.Id);
    }
    private async Task<RemotePerceptualSearchState> Read(Guid id)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var json = await scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().JobLeases.Where(l => l.Id == id).Select(l => l.PerceptualSearchStateJson).SingleAsync();
        return JsonSerializer.Deserialize<RemotePerceptualSearchState>(json!, Json)!;
    }
    private static RemotePerceptualProbeReport Report(RemotePerceptualSearchState state)
    {
        var value = state.AskedValue!.Value;
        RemoteImagePerceptualEvidence Image(string hash, double score) => RemoteImagePerceptualEvidence.From(
            new(new(32, 32, false, 0, score, [score]), null, Source, hash, Tool, 1, MeasurementLocation: "Worker"));
        return new(state.Contract.Id, "libwebp", value, Source, new(Image: Image(Source, 100), ImageMetadata: ImageMetadataResult.Ok(new(false, false))),
            [new(0, value * 10 + 1000, Source, Candidate, Image: Image(Candidate, value >= 60 ? 90 : 80))], EncoderSha256: Tool);
    }

    [Fact]
    public async Task A_worker_that_downgraded_cannot_continue_a_new_protocol_search()
    {
        var (client, id, _) = await Arrange(); using var owned = client;
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var lease = await db.JobLeases.Include(l => l.Worker).SingleAsync(l => l.Id == id);
            lease.Worker!.ProtocolVersion = 10; await db.SaveChangesAsync();
        }
        using var response = await client.PostAsJsonAsync($"/api/workers/leases/{id}/perceptual-probe", Report(await Read(id)));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Empty((await Read(id)).Probes);
    }

    [Theory]
    [InlineData(5368709120L, true)]
    [InlineData(1048576L, false)]
    public async Task A_proved_new_worker_claims_an_image_only_with_space_for_native_search_and_final_verification(long scratchBytes, bool canClaim)
    {
        var (client, oldLeaseId, jobId) = await Arrange(); using var owned = client;
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var old = await db.JobLeases.Include(l => l.Worker).Include(l => l.Job).ThenInclude(j => j!.MediaFile).ThenInclude(m => m!.Library).SingleAsync(l => l.Id == oldLeaseId);
            var media = old.Job!.MediaFile!; var library = media.Library!;
            library.Path = Path.Combine(api.LibraryDirectory, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(library.Path);
            media.Path = Path.Combine(library.Path, "source.png"); await File.WriteAllBytesAsync(media.Path, new byte[300 * 1024]);
            media.SizeBytes = 300 * 1024; media.Status = MediaFileStatus.Probed; media.VideoCodec = "png"; media.FrameCount = 1;
            library.TargetImageFormat = "webp"; library.ImageQuality = 80; library.AdaptiveImageQuality = true;
            library.ImagePerceptualGateEnabled = true; library.MinimumImagePerceptualScore = 85; library.ImageQualityGateEnabled = false;
            library.WorkPlacement = WorkPlacement.WorkerOnly; library.AudioLoudnessGateEnabled = true;
            old.Worker!.FreeScratchBytes = scratchBytes;
            old.Worker.PerceptualCapabilitiesJson = JsonSerializer.Serialize(new WorkerPerceptualCapabilities(["libwebp"], true,
                ImageMetricRevision: ImagePerceptualResultParser.Revision), Json);
            old.Job.Status = JobStatus.Queued; old.Job.SourceSha256 = null;
            db.JobLeases.Remove(old); await db.SaveChangesAsync();
        }
        using var response = await client.PostAsync("/api/workers/claim", null);
        if (!canClaim)
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            await using var refusedScope = api.Services.CreateAsyncScope();
            var job = await refusedScope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().Jobs
                .Include(j => j.MediaFile).SingleAsync(j => j.Id == jobId);
            Assert.Equal(JobStatus.Queued, job.Status); Assert.Equal(0, job.MediaFile!.FailureCount);
            return;
        }
        response.EnsureSuccessStatusCode(); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(jobId, body.GetProperty("jobId").GetInt32()); Assert.Equal((int)MediaKind.Image, body.GetProperty("kind").GetInt32());
        Assert.Equal(80, body.GetProperty("perceptualSearch").GetProperty("value").GetInt32());
        Assert.Equal(4, body.GetProperty("fullVerification").GetProperty("version").GetInt32());
        Assert.False(body.GetProperty("fullVerification").GetProperty("measureAudio").GetBoolean());
        Assert.True(body.GetProperty("requiredScratchBytes").GetInt64() > 128L * 1024 * 1024);
        Assert.DoesNotContain(api.LibraryDirectory, body.GetRawText());
        var state = await Read(body.GetProperty("leaseId").GetGuid());
        Assert.NotNull(state.Encoding); Assert.False(state.CanEncode); Assert.Empty(state.Probes);
        await using var verificationScope = api.Services.CreateAsyncScope();
        var workJson = await verificationScope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().JobLeases
            .Where(l => l.Id == body.GetProperty("leaseId").GetGuid()).Select(l => l.VerificationWorkJson).SingleAsync();
        Assert.Equal("webp", JsonSerializer.Deserialize<QueueDispatcher.JobWork>(workJson!, Json)!.Original.ExpectedVideoCodec);
    }

    [Fact]
    public async Task Samples_advance_once_and_settle_on_frozen_final_arguments_without_using_new_library_settings()
    {
        var (client, id, job) = await Arrange(); using var owned = client;
        for (var index = 0; index < 4; index++)
        {
            var request = Report(await Read(id));
            using var first = await client.PostAsJsonAsync($"/api/workers/leases/{id}/perceptual-probe", request);
            first.EnsureSuccessStatusCode(); var answer = await first.Content.ReadAsStringAsync();
            using var replay = await client.PostAsJsonAsync($"/api/workers/leases/{id}/perceptual-probe", request);
            replay.EnsureSuccessStatusCode(); Assert.Equal(answer, await replay.Content.ReadAsStringAsync());
            Assert.Equal(index + 1, (await Read(id)).Probes.Count);
            if (index == 0)
            {
                await using var changed = api.Services.CreateAsyncScope();
                var db = changed.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
                var libraryId = await db.Jobs.Where(j => j.Id == job).Select(j => j.LibraryId).SingleAsync();
                var library = await db.Libraries.SingleAsync(l => l.Id == libraryId);
                library.ImageQuality = 10; library.TargetImageFormat = "jpeg"; library.MinimumImagePerceptualScore = 99;
                await db.SaveChangesAsync();
            }
        }
        var state = await Read(id); Assert.True(state.CanEncode); Assert.Equal(62, state.Decision!.SelectedValue);
        await using var scope = api.Services.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().Jobs.SingleAsync(j => j.Id == job);
        Assert.Equal(62, row.AdaptiveImageQuality); Assert.NotNull(row.PerceptualSearchJson);
        var workJson = await scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().JobLeases.Where(l => l.Id == id).Select(l => l.VerificationWorkJson).SingleAsync();
        var frozen = JsonSerializer.Deserialize<QueueDispatcher.JobWork>(workJson!, Json);
        Assert.Equal(62, frozen.Spec.ImageQuality); Assert.Contains("62", frozen.Arguments);
    }

    [Fact]
    public async Task Required_source_metadata_failure_ends_without_samples_upload_or_media_failure_budget()
    {
        var (client, id, job) = await Arrange(); using var owned = client;
        var report = Report(await Read(id)); report = report with { Source = report.Source with { ImageMetadata = ImageMetadataResult.Failed("Missing compression module") }, Samples = [] };
        using var result = await client.PostAsJsonAsync($"/api/workers/leases/{id}/perceptual-probe", report);
        result.EnsureSuccessStatusCode();
        var state = await Read(id); Assert.True(state.SourceRejected); Assert.False(state.CanEncode); Assert.Empty(state.Probes);
        await using var scope = api.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var row = await db.Jobs.Include(j => j.MediaFile).SingleAsync(j => j.Id == job);
        Assert.Equal(JobStatus.Failed, row.Status); Assert.Equal(0, row.MediaFile!.FailureCount); Assert.Equal(0, row.QualityRetryCount);
        Assert.Null(row.WorkOutputPath); Assert.NotNull(row.VerificationReportJson);
    }

    [Fact]
    public async Task Concurrent_identical_reports_share_one_probe_and_one_next_question()
    {
        var (client, id, _) = await Arrange(); using var owned = client;
        var request = Report(await Read(id));
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => client.PostAsJsonAsync($"/api/workers/leases/{id}/perceptual-probe", request)));
        try
        {
            foreach (var response in responses) response.EnsureSuccessStatusCode();
            Assert.Equal(await responses[0].Content.ReadAsStringAsync(), await responses[1].Content.ReadAsStringAsync());
            Assert.Single((await Read(id)).Probes);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task Result_and_verification_routes_refuse_a_candidate_until_its_quality_search_settles()
    {
        var (client, id, _) = await Arrange(); using var owned = client;
        client.DefaultRequestHeaders.Add("X-Optimisarr-Source-Sha256", Source);
        client.DefaultRequestHeaders.Add("X-Optimisarr-Candidate-Sha256", Candidate);
        using var result = await client.PostAsync($"/api/workers/leases/{id}/result", new ByteArrayContent([1, 2, 3]));
        using var verification = await client.PostAsJsonAsync($"/api/workers/leases/{id}/verification", new RemoteVerificationEvidence(Guid.NewGuid(), Source, Candidate));
        using var offset = await client.GetAsync($"/api/workers/leases/{id}/result/offset");
        foreach (var response in new[] { result, verification, offset })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("worker.perceptual.notSettled", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        Assert.Empty((await Read(id)).Probes);
    }

    [Fact]
    public async Task Another_worker_and_conflicting_retries_cannot_change_recorded_evidence()
    {
        var (client, id, _) = await Arrange(); using var owned = client;
        var (other, _, _) = await Arrange(); using var ownedOther = other;
        var request = Report(await Read(id));
        using var stranger = await other.PostAsJsonAsync($"/api/workers/leases/{id}/perceptual-probe", request);
        Assert.Equal(HttpStatusCode.Forbidden, stranger.StatusCode); Assert.Empty((await Read(id)).Probes);
        using var accepted = await client.PostAsJsonAsync($"/api/workers/leases/{id}/perceptual-probe", request);
        accepted.EnsureSuccessStatusCode();
        var before = JsonSerializer.Serialize(await Read(id), Json);
        using var conflicting = await client.PostAsJsonAsync($"/api/workers/leases/{id}/perceptual-probe", request with { Error = "different evidence" });
        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
        Assert.Equal(before, JsonSerializer.Serialize(await Read(id), Json));
    }

    [Fact]
    public async Task A_lost_source_refusal_response_can_be_replayed_after_the_lease_is_released()
    {
        var (client, id, _) = await Arrange(); using var owned = client;
        var request = Report(await Read(id)); request = request with { Source = new(Error: "Unsupported source"), Samples = [] };
        using var first = await client.PostAsJsonAsync($"/api/workers/leases/{id}/perceptual-probe", request);
        first.EnsureSuccessStatusCode();
        using var repeated = await client.PostAsJsonAsync($"/api/workers/leases/{id}/perceptual-probe", request);
        repeated.EnsureSuccessStatusCode(); Assert.Equal(await first.Content.ReadAsStringAsync(), await repeated.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_corrupt_encoding_snapshot_cannot_authorise_delivery_even_when_its_search_decision_is_complete()
    {
        var (client, id, _) = await Arrange(); using var owned = client;
        for (var index = 0; index < 4; index++)
            (await client.PostAsJsonAsync($"/api/workers/leases/{id}/perceptual-probe", Report(await Read(id)))).EnsureSuccessStatusCode();
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var lease = await db.JobLeases.SingleAsync(l => l.Id == id);
            var state = JsonSerializer.Deserialize<RemotePerceptualSearchState>(lease.PerceptualSearchStateJson!, Json)!;
            state = state with { Encoding = state.Encoding! with { ContractId = Guid.NewGuid() } };
            lease.PerceptualSearchStateJson = JsonSerializer.Serialize(state, Json); await db.SaveChangesAsync();
        }
        using var response = await client.GetAsync($"/api/workers/leases/{id}/result/offset");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("worker.perceptual.notSettled", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("cancelled")]
    [InlineData("attempt")]
    public async Task Stale_sample_reports_cannot_modify_the_current_attempt(string change)
    {
        var (client, id, job) = await Arrange(); using var owned = client;
        var request = Report(await Read(id)); var before = JsonSerializer.Serialize(await Read(id), Json);
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            if (change == "expired") (await db.JobLeases.SingleAsync(l => l.Id == id)).ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            else { var row = await db.Jobs.SingleAsync(j => j.Id == job); if (change == "cancelled") row.Status = JobStatus.Cancelled; else row.ExecutionAttempt++; }
            await db.SaveChangesAsync();
        }
        using var result = await client.PostAsJsonAsync($"/api/workers/leases/{id}/perceptual-probe", request);
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
        Assert.Equal(before, JsonSerializer.Serialize(await Read(id), Json));
    }
}
