using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Optimisarr.Api.Security;

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
            });
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
