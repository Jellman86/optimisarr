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
    public async Task Each_engine_has_its_own_status_and_an_unknown_one_is_not_found()
    {
        await SetFeatureAsync(false);
        using var client = Admin();

        var realEsrgan = await client.GetFromJsonAsync<JsonObject>("/api/system/super-resolution/realesrgan?model=realesrgan-x4plus&scale=4");
        var realCugan = await client.GetFromJsonAsync<JsonObject>("/api/system/super-resolution/realcugan");
        var original = await client.GetFromJsonAsync<JsonObject>("/api/system/realcugan");
        using var unknown = await client.GetAsync("/api/system/super-resolution/waifu2x");

        Assert.Equal("realesrgan", realEsrgan!["engine"]!.GetValue<string>());
        Assert.Equal("realesrgan-x4plus", realEsrgan["preset"]!.GetValue<string>());
        Assert.Equal("realcugan", realCugan!["engine"]!.GetValue<string>());
        Assert.Equal("realcugan", original!["engine"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A_check_of_a_combination_the_engine_would_misstitch_reports_the_problem()
    {
        await SetFeatureAsync(false);
        using var client = Admin();

        var status = await client.GetFromJsonAsync<JsonObject>("/api/system/super-resolution/realesrgan?model=realesrgan-x4plus&scale=2");

        Assert.NotEqual("Ready", status!["readiness"]!.GetValue<string>());
        Assert.Contains("4x", status["problems"]!.ToJsonString());
    }

    [Fact]
    public async Task Each_engines_probe_refuses_to_run_while_the_feature_is_off()
    {
        await SetFeatureAsync(false);
        using var client = Admin();
        using var response = await client.PostAsync("/api/system/super-resolution/realesrgan/probe", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Real_esrgan_paths_are_saved_with_the_settings_and_a_relative_one_is_refused()
    {
        await SetFeatureAsync(false);
        using var client = Admin();
        var request = (await client.GetFromJsonAsync<JsonObject>("/api/settings"))!.DeepClone().AsObject();
        var binary = Path.Combine(Path.GetTempPath(), "esrgan", "realesrgan-ncnn-vulkan");

        request["superResolution"]!["realEsrganBinaryPath"] = "relative/realesrgan";
        using (var refused = await client.PutAsJsonAsync("/api/settings", request))
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        request["superResolution"]!["realEsrganBinaryPath"] = binary;
        using (var saved = await client.PutAsJsonAsync("/api/settings", request))
            saved.EnsureSuccessStatusCode();
        var after = await client.GetFromJsonAsync<JsonObject>("/api/settings");
        Assert.Equal(binary, after!["superResolution"]!["realEsrganBinaryPath"]!.GetValue<string>());

        request["superResolution"]!["realEsrganBinaryPath"] = "";
        using (var cleared = await client.PutAsJsonAsync("/api/settings", request))
            cleared.EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData("realesrgan", "realesrgan-x4plus", 4, HttpStatusCode.Created)]
    [InlineData(null, null, null, HttpStatusCode.Created)]
    [InlineData("realesrgan", "realesrgan-x4plus", 2, HttpStatusCode.BadRequest)]
    [InlineData("realcugan", "mixed", 2, HttpStatusCode.BadRequest)]
    [InlineData("waifu2x", "any", 2, HttpStatusCode.BadRequest)]
    public async Task A_librarys_model_choice_is_validated_and_returned(string? engine, string? model, int? scale, HttpStatusCode expected)
    {
        using var client = Admin();
        var path = Path.Combine(api.LibraryDirectory, "sr-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        using var response = await client.PostAsJsonAsync("/api/libraries", new
        {
            name = "Model choice", path, mediaType = "Film", ruleProfile = "ConservativeHevc",
            superResolutionEnabled = true, superResolutionEngine = engine, superResolutionModel = model, superResolutionScale = scale,
        });

        Assert.Equal(expected, response.StatusCode);
        if (expected != HttpStatusCode.Created) return;
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(engine, body["superResolutionEngine"]?.GetValue<string>());
        Assert.Equal(model, body["superResolutionModel"]?.GetValue<string>());
        Assert.Equal(scale, body["superResolutionScale"]?.GetValue<int>());
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

    private static object LibraryBody(string path, string? qualityCheck) => new
    {
        name = "Anime " + Guid.NewGuid().ToString("N")[..6], path, mediaType = "Tv", ruleProfile = "ConservativeHevc", enabled = true,
        superResolutionEnabled = true, superResolutionQualityCheck = qualityCheck,
    };

    [Fact]
    public async Task A_library_saves_and_returns_how_it_checks_upscaled_results()
    {
        using var client = Admin();
        var path = Directory.CreateDirectory(Path.Combine(api.LibraryDirectory, "sr-" + Guid.NewGuid().ToString("N"))).FullName;
        using var created = await client.PostAsJsonAsync("/api/libraries", LibraryBody(path, "FullGate"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var library = await created.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("FullGate", library!["superResolutionQualityCheck"]!.GetValue<string>());
        await client.DeleteAsync($"/api/libraries/{library["id"]}");
    }

    [Fact]
    public async Task A_library_without_a_choice_gets_the_broken_frame_check()
    {
        using var client = Admin();
        var path = Directory.CreateDirectory(Path.Combine(api.LibraryDirectory, "sr-" + Guid.NewGuid().ToString("N"))).FullName;
        using var created = await client.PostAsJsonAsync("/api/libraries", LibraryBody(path, null));
        var library = await created.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("BrokenFramesOnly", library!["superResolutionQualityCheck"]!.GetValue<string>());
        await client.DeleteAsync($"/api/libraries/{library["id"]}");
    }

    [Fact]
    public async Task An_unknown_quality_check_is_rejected()
    {
        using var client = Admin();
        var path = Directory.CreateDirectory(Path.Combine(api.LibraryDirectory, "sr-" + Guid.NewGuid().ToString("N"))).FullName;
        using var created = await client.PostAsJsonAsync("/api/libraries", LibraryBody(path, "Vibes"));
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
    }
}
