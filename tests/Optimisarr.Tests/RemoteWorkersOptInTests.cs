using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Optimisarr.Tests;

/// <summary>
/// Remote workers are opt-in by design: the roadmap's constraint is that one container stays the
/// complete, uncomplicated default. These tests hold that line — the feature must be inert until an
/// operator turns it on, and turning it back off must not destroy what was already paired.
/// </summary>
[Collection(TokenedApiCollection.Name)]
public sealed class RemoteWorkersOptInTests
{
    private readonly AdminTokenAuthEndpointTests.TokenedApi _api;

    public RemoteWorkersOptInTests(AdminTokenAuthEndpointTests.TokenedApi api) => _api = api;

    private HttpClient Admin()
    {
        var client = _api.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        return client;
    }

    private async Task SetRemoteWorkers(bool enabled)
    {
        var admin = Admin();
        var current = await (await admin.GetAsync("/api/settings")).Content.ReadFromJsonAsync<JsonElement>();
        using var doc = JsonDocument.Parse(current.GetRawText());
        var payload = new Dictionary<string, object?>();
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            payload[property.Name] = JsonSerializer.Deserialize<object?>(property.Value.GetRawText());
        }
        payload["remoteWorkersEnabled"] = enabled;

        using var saved = await admin.PutAsJsonAsync("/api/settings", payload);
        saved.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Remote_workers_are_off_until_an_operator_turns_them_on()
    {
        var settings = await (await Admin().GetAsync("/api/settings")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(settings.GetProperty("remoteWorkersEnabled").GetBoolean());
    }

    [Fact]
    public async Task While_disabled_a_sidecar_cannot_be_paired()
    {
        await SetRemoteWorkers(false);

        using var code = await Admin().PostAsync("/api/workers/pairing-code", null);
        Assert.Equal(HttpStatusCode.Forbidden, code.StatusCode);

        // And the open pairing route refuses too, so the switch is not merely cosmetic in the UI.
        using var pair = await _api.CreateClient().PostAsJsonAsync("/api/workers/pair", new
        {
            code = "12345678",
            name = "x",
            operatingSystem = "linux",
            architecture = "x64",
            protocolMinimum = 1,
            protocolMaximum = 1,
            vmaf = "Cpu",
            freeScratchBytes = 1L,
            maxConcurrency = 1,
        });
        Assert.Equal(HttpStatusCode.Forbidden, pair.StatusCode);
    }

    [Fact]
    public async Task Turning_it_off_stops_check_ins_without_destroying_the_pairing()
    {
        await SetRemoteWorkers(true);

        var admin = Admin();
        using var issued = await admin.PostAsync("/api/workers/pairing-code", null);
        issued.EnsureSuccessStatusCode();
        var pin = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

        using var paired = await _api.CreateClient().PostAsJsonAsync("/api/workers/pair", new
        {
            code = pin,
            name = "Opt-in worker",
            operatingSystem = "linux",
            architecture = "x64",
            protocolMinimum = 1,
            protocolMaximum = 1,
            vmaf = "Cpu",
            freeScratchBytes = 1L,
            maxConcurrency = 1,
        });
        paired.EnsureSuccessStatusCode();
        var body = await paired.Content.ReadFromJsonAsync<JsonElement>();
        var credential = body.GetProperty("credential").GetString()!;
        var workerId = body.GetProperty("workerId").GetInt32();

        var sidecar = _api.CreateClient();
        sidecar.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        using var beforeOff = await sidecar.PostAsJsonAsync("/api/workers/heartbeat",
            new { freeScratchBytes = 1L, maxConcurrency = 1 });
        Assert.Equal(HttpStatusCode.OK, beforeOff.StatusCode);

        await SetRemoteWorkers(false);

        using var afterOff = await sidecar.PostAsJsonAsync("/api/workers/heartbeat",
            new { freeScratchBytes = 1L, maxConcurrency = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, afterOff.StatusCode);

        // Non-destructive: the record survives so an operator can still see and revoke what is
        // paired, and re-enabling restores the worker rather than requiring a re-pair.
        using var listed = await Admin().GetAsync("/api/workers");
        listed.EnsureSuccessStatusCode();
        var rows = await listed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(rows.EnumerateArray(), w => w.GetProperty("id").GetInt32() == workerId);

        await SetRemoteWorkers(true);
        using var afterOn = await sidecar.PostAsJsonAsync("/api/workers/heartbeat",
            new { freeScratchBytes = 1L, maxConcurrency = 1 });
        Assert.Equal(HttpStatusCode.OK, afterOn.StatusCode);

        await SetRemoteWorkers(false);
    }
}
