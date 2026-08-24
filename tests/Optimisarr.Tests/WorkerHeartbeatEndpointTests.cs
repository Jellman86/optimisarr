using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Optimisarr.Tests;

/// <summary>
/// Drives the whole worker lifecycle against the real host with the admin token configured:
/// issue a PIN, pair without a token, check in with the issued credential, revoke, and prove the
/// credential is dead afterwards. Revocation actually biting is the property worth an end-to-end
/// test — a unit test can only show that an absent fingerprint fails to match, not that the live
/// route consults it.
/// </summary>
public sealed class WorkerHeartbeatEndpointTests
    : IClassFixture<AdminTokenAuthEndpointTests.TokenedApi>
{
    private readonly AdminTokenAuthEndpointTests.TokenedApi _api;

    public WorkerHeartbeatEndpointTests(AdminTokenAuthEndpointTests.TokenedApi api) => _api = api;

    private HttpClient Admin()
    {
        var client = _api.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        return client;
    }

    private static object PairBody(string code, string name) => new
    {
        code,
        name,
        operatingSystem = "linux",
        architecture = "x64",
        protocolMinimum = 1,
        protocolMaximum = 1,
        videoEncoders = new[] { "libx265" },
        hardwareDecoders = Array.Empty<string>(),
        vmaf = "Cpu",
        freeScratchBytes = 50L * 1024 * 1024 * 1024,
        maxConcurrency = 2,
    };

    private static object Beat(long scratch = 1024, int concurrency = 2) => new
    {
        freeScratchBytes = scratch,
        maxConcurrency = concurrency,
    };

    [Fact]
    public async Task A_paired_worker_can_check_in_until_it_is_revoked()
    {
        var admin = Admin();

        using var issued = await admin.PostAsync("/api/workers/pairing-code", null);
        issued.EnsureSuccessStatusCode();
        var code = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

        // Pairing carries no admin token: a sidecar has only the PIN.
        using var paired = await _api.CreateClient()
            .PostAsJsonAsync("/api/workers/pair", PairBody(code, "Heartbeat worker"));
        paired.EnsureSuccessStatusCode();
        var pairBody = await paired.Content.ReadFromJsonAsync<JsonElement>();
        var credential = pairBody.GetProperty("credential").GetString()!;
        var workerId = pairBody.GetProperty("workerId").GetInt32();

        var sidecar = _api.CreateClient();
        sidecar.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);

        using var beat = await sidecar.PostAsJsonAsync("/api/workers/heartbeat", Beat());
        Assert.Equal(HttpStatusCode.OK, beat.StatusCode);
        var beatBody = await beat.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(workerId, beatBody.GetProperty("workerId").GetInt32());
        Assert.True(beatBody.GetProperty("heartbeatIntervalSeconds").GetInt32() > 0);

        // Having just checked in, the worker reads as online to an operator.
        using var listed = await admin.GetAsync("/api/workers");
        var rows = await listed.Content.ReadFromJsonAsync<JsonElement>();
        var row = rows.EnumerateArray().Single(w => w.GetProperty("id").GetInt32() == workerId);
        Assert.True(row.GetProperty("online").GetBoolean());

        using var revoked = await admin.DeleteAsync($"/api/workers/{workerId}");
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        // The credential is now dead. This is the assertion the whole revocation design exists for.
        using var afterRevoke = await sidecar.PostAsJsonAsync("/api/workers/heartbeat", Beat());
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);

        // And a revoked worker never shows as online, whatever its last heartbeat said.
        using var listedAgain = await admin.GetAsync("/api/workers");
        var rowsAgain = await listedAgain.Content.ReadFromJsonAsync<JsonElement>();
        var revokedRow = rowsAgain.EnumerateArray().Single(w => w.GetProperty("id").GetInt32() == workerId);
        Assert.False(revokedRow.GetProperty("online").GetBoolean());
    }

    [Fact]
    public async Task Capabilities_cross_the_wire_as_names_not_numbers()
    {
        // The worker contract is consumed by separately-versioned third-party sidecars, so an
        // enum's meaning must not depend on its ordinal. Renumbering VmafCapability would
        // otherwise silently change what a paired worker is believed to support, and that value
        // gates whether a job may be offered to it.
        var admin = Admin();

        using var issued = await admin.PostAsync("/api/workers/pairing-code", null);
        var code = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

        using var paired = await _api.CreateClient()
            .PostAsJsonAsync("/api/workers/pair", PairBody(code, "Named capability worker"));
        paired.EnsureSuccessStatusCode();
        var workerId = (await paired.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workerId").GetInt32();

        using var listed = await admin.GetAsync("/api/workers");
        var row = (await listed.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().Single(w => w.GetProperty("id").GetInt32() == workerId);

        var vmaf = row.GetProperty("vmaf");
        Assert.Equal(JsonValueKind.String, vmaf.ValueKind);
        Assert.Equal("Cpu", vmaf.GetString());
    }

    [Fact]
    public async Task Pairing_rejects_an_unknown_capability_name_and_says_what_is_valid()
    {
        var admin = Admin();
        using var issued = await admin.PostAsync("/api/workers/pairing-code", null);
        var code = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

        using var response = await _api.CreateClient().PostAsJsonAsync("/api/workers/pair", new
        {
            code,
            name = "Bad capability",
            operatingSystem = "linux",
            architecture = "x64",
            protocolMinimum = 1,
            protocolMaximum = 1,
            vmaf = "gpu",
            freeScratchBytes = 1L,
            maxConcurrency = 1,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // Naming the valid values matters: this contract is hand-implemented by sidecar authors.
        Assert.Contains("Cuda", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Heartbeat_rejects_a_missing_or_unknown_credential()
    {
        using var none = await _api.CreateClient().PostAsJsonAsync("/api/workers/heartbeat", Beat());
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);

        var bogus = _api.CreateClient();
        bogus.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "not-a-real-worker-credential");
        using var wrong = await bogus.PostAsJsonAsync("/api/workers/heartbeat", Beat());
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public async Task The_admin_token_does_not_authenticate_a_worker()
    {
        // The two credentials authorise different things. An admin token is not a worker identity,
        // and accepting it here would let anything holding it impersonate a paired machine.
        using var response = await Admin().PostAsJsonAsync("/api/workers/heartbeat", Beat());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
