using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Api.Library;
using Optimisarr.Core.SuperResolution;
using Optimisarr.Data;

namespace Optimisarr.Tests;

/// <summary>The experimental switch gates the engine's HTTP surface, and a rejected settings save
/// changes nothing.</summary>
[Collection(TokenedApiCollection.Name)]
public sealed class SuperResolutionEndpointTests(AdminTokenAuthEndpointTests.TokenedApi api)
{
    private HttpClient Admin()
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        return client;
    }

    private async Task SetFeatureAsync(bool enabled)
    {
        using var scope = api.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
        await settings.SetSuperResolutionOptionsAsync(SuperResolutionOptions.Defaults with { Enabled = enabled }, CancellationToken.None);
    }

    [Fact]
    public async Task The_engine_probe_refuses_to_run_while_the_feature_is_off()
    {
        await SetFeatureAsync(false);
        using var client = Admin();
        using var response = await client.PostAsync("/api/system/realcugan/probe", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("superResolution.disabled", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_settings_save_with_an_invalid_super_resolution_section_changes_nothing()
    {
        await SetFeatureAsync(false);
        using var client = Admin();
        var before = await client.GetFromJsonAsync<JsonObject>("/api/settings");
        var request = before!.DeepClone().AsObject();
        request["dryRunMode"] = !before["dryRunMode"]!.GetValue<bool>();
        request["superResolution"]!["enabled"] = true;
        request["superResolution"]!["scale"] = 7;

        using var response = await client.PutAsJsonAsync("/api/settings", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var after = await client.GetFromJsonAsync<JsonObject>("/api/settings");
        Assert.Equal(before["dryRunMode"]!.GetValue<bool>(), after!["dryRunMode"]!.GetValue<bool>());
        Assert.False(after["superResolution"]!["enabled"]!.GetValue<bool>());
    }
}
