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
[Collection(TokenedApiCollection.Name)]
public sealed class WorkerHeartbeatEndpointTests
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


    /// <summary>
    /// Sets the opt-in explicitly rather than relying on whatever a previous test left behind, so
    /// these tests do not depend on execution order.
    /// </summary>
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

    private static object Beat(long scratch = 1024, int concurrency = 2) => new
    {
        freeScratchBytes = scratch,
        maxConcurrency = concurrency,
    };

    [Fact]
    public async Task A_paired_worker_can_check_in_until_it_is_revoked()
    {
        await EnableRemoteWorkers();
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
        await EnableRemoteWorkers();
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
    public async Task Heartbeat_renegotiates_upgraded_and_downgraded_sidecars_without_repairing()
    {
        await EnableRemoteWorkers();
        var pin = await (await Admin().PostAsync("/api/workers/pairing-code", null)).Content.ReadFromJsonAsync<JsonElement>();
        var pair = await (await _api.CreateClient().PostAsJsonAsync("/api/workers/pair",
            PairBody(pin.GetProperty("code").GetString()!, "Protocol upgrade"))).Content.ReadFromJsonAsync<JsonElement>();
        using var client = _api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pair.GetProperty("credential").GetString());
        foreach (var maximum in new[] { 2, 1, 2 })
        {
            using var response = await client.PostAsJsonAsync("/api/workers/heartbeat",
                new { freeScratchBytes = 1024, maxConcurrency = 1, protocolMinimum = 1, protocolMaximum = maximum });
            response.EnsureSuccessStatusCode();
            Assert.Equal(maximum, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("protocolVersion").GetInt32());
        }
        // Protocol-1 clients predate range reporting; a downgrade must not inherit protocol 2.
        using var legacy = await client.PostAsJsonAsync("/api/workers/heartbeat", Beat());
        Assert.Equal(1, (await legacy.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("protocolVersion").GetInt32());
        using var incompatible = await client.PostAsJsonAsync("/api/workers/heartbeat",
            new { freeScratchBytes = 1024, maxConcurrency = 1, protocolMinimum = 99, protocolMaximum = 99 });
        Assert.Equal(HttpStatusCode.Conflict, incompatible.StatusCode);
    }

    [Fact]
    public async Task A_sidecar_upgrading_itself_is_visible_without_re_pairing()
    {
        await EnableRemoteWorkers();
        // The whole point of the field: upgrading a sidecar does not re-pair it, so a build
        // recorded only at pairing would be wrong from the first upgrade onwards and quietly stay
        // wrong — which is exactly how a machine ran a build nobody realised was stale.
        var admin = Admin();

        using var issued = await admin.PostAsync("/api/workers/pairing-code", null);
        var code = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

        var body = (Dictionary<string, object?>)PairBodyWith(code, "Upgrading worker", "0.1.5 (202609131900)");
        using var paired = await _api.CreateClient().PostAsJsonAsync("/api/workers/pair", body);
        paired.EnsureSuccessStatusCode();
        var pairing = await paired.Content.ReadFromJsonAsync<JsonElement>();
        var workerId = pairing.GetProperty("workerId").GetInt32();
        var credential = pairing.GetProperty("credential").GetString()!;

        Assert.Equal("0.1.5 (202609131900)", await ReportedVersion(admin, workerId));

        var worker = _api.CreateClient();
        worker.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);

        // The upgraded build checks in.
        using var upgraded = await worker.PostAsJsonAsync("/api/workers/heartbeat", new
        {
            freeScratchBytes = 1024L,
            maxConcurrency = 2,
            sidecarVersion = "0.1.6 (202609141130)",
        });
        upgraded.EnsureSuccessStatusCode();
        Assert.Equal("0.1.6 (202609141130)", await ReportedVersion(admin, workerId));

        // A check-in that omits it leaves the recorded build alone rather than blanking it: an
        // older sidecar saying nothing must not erase what a newer one already reported.
        using var silent = await worker.PostAsJsonAsync("/api/workers/heartbeat", Beat());
        silent.EnsureSuccessStatusCode();
        Assert.Equal("0.1.6 (202609141130)", await ReportedVersion(admin, workerId));
    }

    [Fact]
    public async Task A_sidecar_that_reports_no_build_pairs_and_reads_as_empty()
    {
        await EnableRemoteWorkers();
        // A sidecar written against the earlier contract must still pair. It reads as empty rather
        // than as some assumed version, and the UI shows that absence rather than inventing one.
        var admin = Admin();

        using var issued = await admin.PostAsync("/api/workers/pairing-code", null);
        var code = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

        using var paired = await _api.CreateClient()
            .PostAsJsonAsync("/api/workers/pair", PairBody(code, "Older sidecar"));
        paired.EnsureSuccessStatusCode();
        var workerId = (await paired.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("workerId").GetInt32();

        Assert.Equal(string.Empty, await ReportedVersion(admin, workerId));
    }

    [Fact]
    public async Task A_worker_reports_how_busy_its_machine_is()
    {
        await EnableRemoteWorkers();
        var admin = Admin();

        using var issued = await admin.PostAsync("/api/workers/pairing-code", null);
        var code = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        using var paired = await _api.CreateClient()
            .PostAsJsonAsync("/api/workers/pair", PairBody(code, "Busy worker"));
        paired.EnsureSuccessStatusCode();
        var pairing = await paired.Content.ReadFromJsonAsync<JsonElement>();
        var workerId = pairing.GetProperty("workerId").GetInt32();
        var worker = _api.CreateClient();
        worker.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", pairing.GetProperty("credential").GetString()!);

        // Nothing said yet, so nothing is claimed on its behalf.
        var (cpu, gpu) = await ReportedLoad(admin, workerId);
        Assert.Null(cpu);
        Assert.Null(gpu);

        using var busy = await worker.PostAsJsonAsync("/api/workers/heartbeat", new
        {
            freeScratchBytes = 1024L,
            maxConcurrency = 2,
            cpuBusyFraction = 0.87,
            gpuBusyFraction = 0.42,
        });
        busy.EnsureSuccessStatusCode();
        (cpu, gpu) = await ReportedLoad(admin, workerId);
        Assert.Equal(0.87, cpu);
        Assert.Equal(0.42, gpu);

        // A check-in that says nothing about load leaves the last reading alone. A machine that
        // could not read its counters this once has not stopped being busy, and blanking the figure
        // would show an idle Mac mid-encode.
        using var quiet = await worker.PostAsJsonAsync("/api/workers/heartbeat", Beat());
        quiet.EnsureSuccessStatusCode();
        (cpu, gpu) = await ReportedLoad(admin, workerId);
        Assert.Equal(0.87, cpu);
        Assert.Equal(0.42, gpu);

        // One figure without the other is a normal state: a worker with no readable accelerator
        // still knows its CPU.
        using var cpuOnly = await worker.PostAsJsonAsync("/api/workers/heartbeat", new
        {
            freeScratchBytes = 1024L,
            maxConcurrency = 2,
            cpuBusyFraction = 0.1,
        });
        cpuOnly.EnsureSuccessStatusCode();
        (cpu, gpu) = await ReportedLoad(admin, workerId);
        Assert.Equal(0.1, cpu);
        Assert.Equal(0.42, gpu);
    }

    [Fact]
    public async Task A_load_figure_outside_nought_to_one_is_refused_rather_than_clamped()
    {
        await EnableRemoteWorkers();
        // Clamping would record a confident 100% for a sender that is plainly confused, and hide
        // the bug behind a plausible number.
        var admin = Admin();
        using var issued = await admin.PostAsync("/api/workers/pairing-code", null);
        var code = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        using var paired = await _api.CreateClient()
            .PostAsJsonAsync("/api/workers/pair", PairBody(code, "Confused worker"));
        var pairing = await paired.Content.ReadFromJsonAsync<JsonElement>();
        var workerId = pairing.GetProperty("workerId").GetInt32();
        var worker = _api.CreateClient();
        worker.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", pairing.GetProperty("credential").GetString()!);

        using var nonsense = await worker.PostAsJsonAsync("/api/workers/heartbeat", new
        {
            freeScratchBytes = 1024L,
            maxConcurrency = 2,
            cpuBusyFraction = 4.2,
            gpuBusyFraction = -1.0,
        });
        // The check-in still succeeds — a bad load figure is not a reason to refuse a worker.
        nonsense.EnsureSuccessStatusCode();
        var (cpu, gpu) = await ReportedLoad(admin, workerId);
        Assert.Null(cpu);
        Assert.Null(gpu);
    }

    private async Task<(double? Cpu, double? Gpu)> ReportedLoad(HttpClient admin, int workerId)
    {
        using var listed = await admin.GetAsync("/api/workers");
        var row = (await listed.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().Single(w => w.GetProperty("id").GetInt32() == workerId);
        static double? Read(JsonElement row, string name) =>
            row.GetProperty(name).ValueKind == JsonValueKind.Null
                ? null
                : row.GetProperty(name).GetDouble();
        return (Read(row, "cpuBusyFraction"), Read(row, "gpuBusyFraction"));
    }

    private static object PairBodyWith(string code, string name, string sidecarVersion)
    {
        var body = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["name"] = name,
            ["operatingSystem"] = "macos",
            ["architecture"] = "arm64",
            ["protocolMinimum"] = 1,
            ["protocolMaximum"] = 1,
            ["videoEncoders"] = new[] { "libx265" },
            ["hardwareDecoders"] = Array.Empty<string>(),
            ["vmaf"] = "Cpu",
            ["freeScratchBytes"] = 50L * 1024 * 1024 * 1024,
            ["maxConcurrency"] = 2,
            ["sidecarVersion"] = sidecarVersion,
        };
        return body;
    }

    private async Task<string?> ReportedVersion(HttpClient admin, int workerId)
    {
        using var listed = await admin.GetAsync("/api/workers");
        return (await listed.Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray()
            .Single(w => w.GetProperty("id").GetInt32() == workerId)
            .GetProperty("sidecarVersion")
            .GetString();
    }

    [Fact]
    public async Task Pairing_rejects_an_unknown_capability_name_and_says_what_is_valid()
    {
        await EnableRemoteWorkers();
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
        await EnableRemoteWorkers();
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
        await EnableRemoteWorkers();

        // The two credentials authorise different things. An admin token is not a worker identity,
        // and accepting it here would let anything holding it impersonate a paired machine.
        using var response = await Admin().PostAsJsonAsync("/api/workers/heartbeat", Beat());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
