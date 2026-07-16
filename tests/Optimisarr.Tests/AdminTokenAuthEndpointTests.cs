using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Optimisarr.Api.Library;
using Optimisarr.Api.Security;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Tests;

/// <summary>
/// Spins up the real API with <c>OPTIMISARR_ADMIN_TOKEN</c> set and asserts the admin-token
/// middleware actually enforces auth end to end: every destructive/secret-bearing endpoint is
/// rejected without the token, open endpoints stay reachable, and a valid token passes. The
/// no-token rejections never reach the endpoint (the middleware short-circuits), so they don't
/// depend on database state.
/// </summary>
public sealed class AdminTokenAuthEndpointTests : IClassFixture<AdminTokenAuthEndpointTests.TokenedApi>
{
    private readonly TokenedApi _api;

    public AdminTokenAuthEndpointTests(TokenedApi api) => _api = api;

    [Theory]
    [InlineData("GET", "/api/settings")]
    [InlineData("PUT", "/api/settings")]
    [InlineData("GET", "/api/settings/export")]   // contains provider secrets
    [InlineData("POST", "/api/settings/import")]
    [InlineData("GET", "/api/setup")]
    [InlineData("GET", "/api/setup/readiness")]
    [InlineData("PUT", "/api/setup/progress")]
    [InlineData("POST", "/api/setup/complete")]
    [InlineData("POST", "/api/setup/apply")]
    [InlineData("POST", "/api/setup/restart")]
    [InlineData("POST", "/api/libraries")]
    [InlineData("DELETE", "/api/libraries/1")]
    [InlineData("POST", "/api/libraries/1/enqueue")]
    [InlineData("POST", "/api/libraries/1/calibration")]
    [InlineData("POST", "/api/calibration/00000000-0000-0000-0000-000000000000/answers")]
    [InlineData("POST", "/api/calibration/00000000-0000-0000-0000-000000000000/reveal")]
    [InlineData("POST", "/api/calibration/00000000-0000-0000-0000-000000000000/apply")]
    [InlineData("DELETE", "/api/calibration/00000000-0000-0000-0000-000000000000")]
    [InlineData("POST", "/api/jobs/clear")]
    [InlineData("POST", "/api/jobs/clear-pending")]
    [InlineData("POST", "/api/jobs/1/cancel")]
    [InlineData("POST", "/api/jobs/1/retry")]
    [InlineData("DELETE", "/api/jobs/1")]
    [InlineData("POST", "/api/jobs/1/replace")]
    [InlineData("POST", "/api/replacements/1/rollback")]
    [InlineData("POST", "/api/replacements/1/approve")]
    [InlineData("GET", "/api/diagnostics")]       // admin support snapshot
    public async Task A_protected_endpoint_is_401_without_the_token(string method, string path)
    {
        using var response = await _api.CreateClient()
            .SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/api/ready")]
    [InlineData("/api/auth/status")]
    public async Task An_open_endpoint_is_reachable_without_the_token(string path)
    {
        using var response = await _api.CreateClient().GetAsync(path);

        // /api/ready may be 503 in the test host (no /work, /trash), but it must never be 401.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_token_passes_authentication()
    {
        var client = _api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TokenedApi.Token);

        using var response = await client.GetAsync("/api/settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_bearer_request_establishes_an_httponly_media_session()
    {
        var client = _api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TokenedApi.Token);
        using var login = await client.GetAsync("/api/settings");
        var setCookie = login.Headers.GetValues("Set-Cookie").Single();
        var cookiePair = setCookie.Split(';', 2)[0];

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/settings");
        request.Headers.Add("Cookie", cookiePair);
        client.DefaultRequestHeaders.Authorization = null;
        using var response = await client.SendAsync(request);

        Assert.DoesNotContain(TokenedApi.Token, setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_wrong_token_is_rejected()
    {
        var client = _api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-token");

        using var response = await client.GetAsync("/api/settings");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("\"code\":\"auth.required\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Auth_status_advertises_that_a_token_is_required()
    {
        using var response = await _api.CreateClient().GetAsync("/api/auth/status");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"required\":true", body);
    }

    [Fact]
    public async Task Setup_progress_is_resumable_ordered_idempotent_and_restartable()
    {
        var client = _api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TokenedApi.Token);

        using var restartFirst = await client.PostAsync("/api/setup/restart", content: null);
        Assert.Equal(HttpStatusCode.OK, restartFirst.StatusCode);

        using var first = await client.PutAsJsonAsync("/api/setup/progress", new { completedStep = 1 });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("\"currentStep\":2", await first.Content.ReadAsStringAsync());

        using var repeated = await client.PutAsJsonAsync("/api/setup/progress", new { completedStep = 1 });
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);

        using var skipped = await client.PutAsJsonAsync("/api/setup/progress", new { completedStep = 3 });
        Assert.Equal(HttpStatusCode.BadRequest, skipped.StatusCode);
        Assert.Contains("setup.step.invalid", await skipped.Content.ReadAsStringAsync());

        foreach (var completedStep in new[] { 2, 3, 4 })
        {
            using var progress = await client.PutAsJsonAsync("/api/setup/progress", new { completedStep });
            Assert.Equal(HttpStatusCode.OK, progress.StatusCode);
        }

        using var completed = await client.PostAsync("/api/setup/complete", content: null);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Contains("\"completed\":true", await completed.Content.ReadAsStringAsync());

        using var restart = await client.PostAsync("/api/setup/restart", content: null);
        Assert.Equal(HttpStatusCode.OK, restart.StatusCode);
        Assert.Contains("\"currentStep\":1", await restart.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Setup_plan_is_validated_then_applied_atomically_and_duplicate_submission_is_idempotent()
    {
        var client = _api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TokenedApi.Token);
        var sourcePath = Path.Combine(_api.LibraryDirectory, "original-source.mkv");
        await File.WriteAllTextAsync(sourcePath, "original-must-not-change");

        using var library = await client.PostAsJsonAsync("/api/libraries", new
        {
            name = "Setup plan library",
            path = _api.LibraryDirectory,
            mediaType = "Film",
            ruleProfile = "ConservativeHevc"
        });
        Assert.True(
            library.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict,
            await library.Content.ReadAsStringAsync());

        using var restart = await client.PostAsync("/api/setup/restart", content: null);
        Assert.Equal(HttpStatusCode.OK, restart.StatusCode);

        var originalSettings = JsonNode.Parse(await client.GetStringAsync("/api/settings"))!.AsObject();
        var originalConcurrency = originalSettings["maxConcurrentJobs"]!.GetValue<int>();
        var changedSettings = originalSettings.DeepClone().AsObject();
        changedSettings["maxConcurrentJobs"] = originalConcurrency + 1;
        var request = new
        {
            settings = changedSettings,
            useRecommendedEncoder = false,
            applyRecommendedVmaf = false,
            applyRecommendedSchedule = true
        };

        using var premature = await client.PostAsJsonAsync("/api/setup/apply", request);
        Assert.Equal(HttpStatusCode.BadRequest, premature.StatusCode);
        var unchanged = JsonNode.Parse(await client.GetStringAsync("/api/settings"))!.AsObject();
        Assert.Equal(originalConcurrency, unchanged["maxConcurrentJobs"]!.GetValue<int>());

        foreach (var completedStep in new[] { 1, 2, 3, 4 })
        {
            using var progress = await client.PutAsJsonAsync("/api/setup/progress", new { completedStep });
            Assert.Equal(HttpStatusCode.OK, progress.StatusCode);
        }

        using var applied = await client.PostAsJsonAsync("/api/setup/apply", request);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var receipt = await applied.Content.ReadAsStringAsync();
        Assert.Contains("\"completed\":true", receipt);
        Assert.Contains("\"settingsApplied\":true", receipt);
        Assert.Contains("\"recommendationsApplied\":true", receipt);

        var saved = JsonNode.Parse(await client.GetStringAsync("/api/settings"))!.AsObject();
        Assert.Equal(originalConcurrency + 1, saved["maxConcurrentJobs"]!.GetValue<int>());
        var savedLibraries = JsonNode.Parse(await client.GetStringAsync("/api/libraries"))!.AsArray();
        var savedLibrary = savedLibraries
            .Select(node => node!.AsObject())
            .Single(node => node["name"]!.GetValue<string>() == "Setup plan library");
        Assert.Equal("01:00", savedLibrary["autoEnqueueWindowStart"]!.GetValue<string>());
        Assert.Equal("06:00", savedLibrary["autoEnqueueWindowEnd"]!.GetValue<string>());

        changedSettings["maxConcurrentJobs"] = originalConcurrency + 2;
        using var duplicate = await client.PostAsJsonAsync("/api/setup/apply", request);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Contains("\"alreadyApplied\":true", await duplicate.Content.ReadAsStringAsync());
        var stillSaved = JsonNode.Parse(await client.GetStringAsync("/api/settings"))!.AsObject();
        Assert.Equal(originalConcurrency + 1, stillSaved["maxConcurrentJobs"]!.GetValue<int>());
        Assert.Equal("original-must-not-change", await File.ReadAllTextAsync(sourcePath));
    }

    [Fact]
    public async Task Calibration_creates_only_disposable_clipped_jobs_and_delete_cleans_the_session()
    {
        var client = _api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TokenedApi.Token);
        var calibrationDirectory = Path.Combine(
            Path.GetDirectoryName(_api.LibraryDirectory)!,
            "calibration-library-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(calibrationDirectory);
        var sourcePath = Path.Combine(calibrationDirectory, "calibration-source.mkv");
        await File.WriteAllTextAsync(sourcePath, "calibration-source-must-remain-unchanged");

        using var createLibrary = await client.PostAsJsonAsync("/api/libraries", new
        {
            name = "Calibration library",
            path = calibrationDirectory,
            mediaType = "Film",
            ruleProfile = "ConservativeHevc"
        });
        Assert.Equal(HttpStatusCode.Created, createLibrary.StatusCode);
        var libraryId = JsonNode.Parse(await createLibrary.Content.ReadAsStringAsync())!["id"]!.GetValue<int>();

        int mediaFileId;
        using (var scope = _api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var media = new MediaFile
            {
                LibraryId = libraryId,
                Path = sourcePath,
                RelativePath = "calibration-source.mkv",
                SizeBytes = new FileInfo(sourcePath).Length,
                ModifiedAt = DateTimeOffset.UtcNow,
                Status = MediaFileStatus.Probed,
                MediaKind = MediaKind.Video,
                DurationSeconds = 1_200,
                VideoCodec = "h264",
                Width = 1_920,
                Height = 1_080,
                ProbedAt = DateTimeOffset.UtcNow
            };
            db.MediaFiles.Add(media);
            await db.SaveChangesAsync();
            mediaFileId = media.Id;
        }

        using var started = await client.PostAsJsonAsync(
            $"/api/libraries/{libraryId}/calibration",
            new { mediaFileId });
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var sessionId = JsonNode.Parse(await started.Content.ReadAsStringAsync())!["id"]!.GetValue<Guid>();

        using (var clearedPending = await client.PostAsync("/api/jobs/clear-pending", content: null))
        {
            Assert.Equal(HttpStatusCode.OK, clearedPending.StatusCode);
            Assert.Equal(
                0,
                JsonNode.Parse(await clearedPending.Content.ReadAsStringAsync())!["cleared"]!.GetValue<int>());
        }

        using (var scope = _api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var jobs = db.Jobs.Where(job => job.CalibrationSessionId == sessionId).ToList();
            Assert.Equal(15, jobs.Count);
            Assert.All(jobs, job =>
            {
                Assert.Equal(JobType.Calibration, job.Type);
                Assert.Null(job.LibraryId);
                Assert.Equal(12, job.CalibrationClipSeconds);
                Assert.NotNull(job.RequestedVideoQuality);
                Assert.Equal(JobStatus.Queued, job.Status);
            });
            foreach (var job in jobs)
            {
                var workDirectory = Path.Combine(calibrationDirectory, $"job-{job.Id}");
                Directory.CreateDirectory(workDirectory);
                job.WorkOutputPath = Path.Combine(workDirectory, "candidate.mp4");
                await File.WriteAllTextAsync(job.WorkOutputPath, "candidate-clip");
                await File.WriteAllTextAsync(
                    Path.Combine(workDirectory, ".optimisarr-comparison-reference.mkv"),
                    "reference-clip");
                job.Status = JobStatus.Completed;
                job.Progress = 1;
                job.OutputSizeBytes = 1_000_000;
                job.VideoEncoder = "libx265";
                job.VideoQualityMode = "CRF";
                job.EffectiveVideoQuality = job.RequestedVideoQuality;
            }
            await db.SaveChangesAsync();
        }
        using (var clearedFinished = await client.PostAsync("/api/jobs/clear?scope=finished", content: null))
        {
            Assert.Equal(HttpStatusCode.OK, clearedFinished.StatusCode);
            Assert.Equal(
                0,
                JsonNode.Parse(await clearedFinished.Content.ReadAsStringAsync())!["cleared"]!.GetValue<int>());
        }
        using (var scope = _api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            Assert.Equal(15, db.Jobs.Count(job => job.CalibrationSessionId == sessionId));
        }
        Assert.Equal("calibration-source-must-remain-unchanged", await File.ReadAllTextAsync(sourcePath));

        var session = JsonNode.Parse(await client.GetStringAsync($"/api/calibration/{sessionId}"))!.AsObject();
        var initialTrial = session["trial"]!.AsObject();
        Assert.Equal(
            "candidate-clip",
            await client.GetStringAsync(initialTrial["a"]!["url"]!.GetValue<string>()));
        Assert.Equal(
            "reference-clip",
            await client.GetStringAsync(initialTrial["b"]!["url"]!.GetValue<string>()));
        Assert.Equal(
            "reference-clip",
            await client.GetStringAsync(initialTrial["x"]!["url"]!.GetValue<string>()));
        var answerCount = 0;
        while (session["trial"] is JsonObject trial && answerCount < 20)
        {
            Assert.Equal(0, trial["a"]!["startSeconds"]!.GetValue<double>());
            Assert.Equal(0, trial["b"]!["startSeconds"]!.GetValue<double>());
            Assert.Equal(0, trial["x"]!["startSeconds"]!.GetValue<double>());
            using var answered = await client.PostAsJsonAsync($"/api/calibration/{sessionId}/answers", new
            {
                trialId = trial["id"]!.GetValue<Guid>(),
                // The test randomizer always makes B correct. Deliberately wrong answers drive
                // the no-reliable-difference path without exposing that mapping through the API.
                choice = "A"
            });
            Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
            session = JsonNode.Parse(await answered.Content.ReadAsStringAsync())!.AsObject();
            answerCount++;
        }
        Assert.Equal("Complete", session["status"]!.GetValue<string>());
        Assert.Equal(13, answerCount);

        using var revealed = await client.PostAsync($"/api/calibration/{sessionId}/reveal", content: null);
        Assert.Equal(HttpStatusCode.OK, revealed.StatusCode);
        Assert.Equal(30, JsonNode.Parse(await revealed.Content.ReadAsStringAsync())!["result"]!["recommendedQuality"]!.GetValue<int>());

        using var applied = await client.PostAsync($"/api/calibration/{sessionId}/apply", content: null);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        using (var scope = _api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            Assert.Equal(30, db.Libraries.Single(library => library.Id == libraryId).QualityCrf);
            Assert.False(db.Jobs.Any(job => job.Type == JobType.Normal && job.MediaFileId == mediaFileId));
        }

        using var deleted = await client.DeleteAsync($"/api/calibration/{sessionId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using (var scope = _api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            Assert.False(db.Jobs.Any(job => job.CalibrationSessionId == sessionId));
        }
        Assert.Equal("calibration-source-must-remain-unchanged", await File.ReadAllTextAsync(sourcePath));
    }

    /// <summary>
    /// The real app with the admin token configured, a throwaway config directory (fresh migrated
    /// SQLite), and no background workers — the auth tests only exercise the HTTP pipeline.
    /// </summary>
    public sealed class TokenedApi : WebApplicationFactory<Program>
    {
        public const string Token = "test-admin-token-0123456789";
        private readonly string _configDir =
            Path.Combine(Path.GetTempPath(), "optimisarr-authtest-" + Guid.NewGuid().ToString("N"));

        public string LibraryDirectory { get; }

        public TokenedApi()
        {
            Directory.CreateDirectory(_configDir);
            LibraryDirectory = Path.Combine(_configDir, "library");
            Directory.CreateDirectory(LibraryDirectory);
            Environment.SetEnvironmentVariable(AdminTokenAuth.EnvironmentVariable, Token);
            Environment.SetEnvironmentVariable("OPTIMISARR_CONFIG_DIR", _configDir);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureTestServices(services =>
            {
                foreach (var hosted in services.Where(s => s.ServiceType == typeof(IHostedService)).ToList())
                {
                    services.Remove(hosted);
                }
                foreach (var randomizer in services
                    .Where(service => service.ServiceType == typeof(ICalibrationRandomizer))
                    .ToList())
                {
                    services.Remove(randomizer);
                }
                services.AddSingleton<ICalibrationRandomizer, FixedCalibrationRandomizer>();
            });
        }

        private sealed class FixedCalibrationRandomizer : ICalibrationRandomizer
        {
            public bool NextBit() => false;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable(AdminTokenAuth.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("OPTIMISARR_CONFIG_DIR", null);
            try { Directory.Delete(_configDir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }
}
