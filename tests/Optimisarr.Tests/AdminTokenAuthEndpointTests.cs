using System.Net;
using System.Net.Http.Headers;
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

    /// <summary>
    /// The real app with the admin token configured, a throwaway config directory (fresh migrated
    /// SQLite), and no background workers — the auth tests only exercise the HTTP pipeline.
    /// </summary>
    public sealed class TokenedApi : WebApplicationFactory<Program>
    {
        public const string Token = "test-admin-token-0123456789";
        private readonly string _configDir =
            Path.Combine(Path.GetTempPath(), "optimisarr-authtest-" + Guid.NewGuid().ToString("N"));

        public TokenedApi()
        {
            Directory.CreateDirectory(_configDir);
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
