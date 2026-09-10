using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Optimisarr.Api.Workers;

namespace Optimisarr.Tests;

/// <summary>
/// Remote workers are groundwork in this release. Without the experimental flag the switch must not
/// exist, the setting must not be acceptable, and every worker route must refuse, whatever an older
/// database may have stored. With the flag, the opt-in behaves as it always did.
/// </summary>
[Collection(TokenedApiCollection.Name)]
public sealed class RemoteWorkersAvailabilityTests
{
    private readonly AdminTokenAuthEndpointTests.TokenedApi _api;

    public RemoteWorkersAvailabilityTests(AdminTokenAuthEndpointTests.TokenedApi api) => _api = api;

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData(" on ", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_flag_accepts_the_spellings_people_type(string? value, bool expected) =>
        Assert.Equal(expected, RemoteWorkersFeature.IsEnabled(value));

    private WebApplicationFactory<Program> WithoutThePreview() =>
        _api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<RemoteWorkersFeature>();
            services.AddSingleton(new RemoteWorkersFeature(Available: false));
        }));

    private static HttpClient Admin(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        return client;
    }

    [Fact]
    public async Task Without_the_flag_settings_say_workers_are_not_available()
    {
        using var host = WithoutThePreview();
        var settings = await (await Admin(host).GetAsync("/api/settings")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(settings.GetProperty("remoteWorkersAvailable").GetBoolean());
    }

    [Fact]
    public async Task With_the_flag_settings_say_workers_are_available()
    {
        var settings = await (await Admin(_api).GetAsync("/api/settings")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(settings.GetProperty("remoteWorkersAvailable").GetBoolean());
    }

    [Fact]
    public async Task Without_the_flag_the_switch_cannot_be_turned_on()
    {
        using var host = WithoutThePreview();
        var admin = Admin(host);
        var current = await (await admin.GetAsync("/api/settings")).Content.ReadFromJsonAsync<JsonElement>();
        using var doc = JsonDocument.Parse(current.GetRawText());
        var payload = new Dictionary<string, object?>();
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            payload[property.Name] = JsonSerializer.Deserialize<object?>(property.Value.GetRawText());
        }
        payload["remoteWorkersEnabled"] = true;

        using var saved = await admin.PutAsJsonAsync("/api/settings", payload);

        Assert.Equal(HttpStatusCode.BadRequest, saved.StatusCode);
        var error = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("workers.unavailable", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Without_the_flag_every_worker_route_refuses()
    {
        using var host = WithoutThePreview();
        var admin = Admin(host);

        using var pairing = await admin.PostAsync("/api/workers/pairing-code", null);
        Assert.Equal(HttpStatusCode.Forbidden, pairing.StatusCode);
        var error = await pairing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("workers.unavailable", error.GetProperty("code").GetString());
    }
}
