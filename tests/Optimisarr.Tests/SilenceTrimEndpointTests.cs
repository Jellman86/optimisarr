using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Optimisarr.Tests;

/// <summary>Saving a music library holds silence trimming to the same rules as its stored settings.</summary>
[Collection(TokenedApiCollection.Name)]
public sealed class SilenceTrimEndpointTests(AdminTokenAuthEndpointTests.TokenedApi api)
{
    [Fact]
    public async Task An_update_that_leaves_out_stored_audio_quality_reporting_cannot_switch_trimming_on_beside_it()
    {
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        var path = Directory.CreateDirectory(Path.Combine(api.LibraryDirectory, "trim-" + Guid.NewGuid().ToString("N"))).FullName;
        var body = new Dictionary<string, object?>
        {
            ["name"] = "Trim music", ["path"] = path, ["mediaType"] = "Music", ["ruleProfile"] = "ConservativeHevc",
        };
        using var created = await client.PostAsJsonAsync("/api/libraries", new Dictionary<string, object?>(body) { ["audioQualityReportingEnabled"] = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["id"]!.GetValue<int>();
        try
        {
            using var refused = await client.PutAsJsonAsync($"/api/libraries/{id}", new Dictionary<string, object?>(body) { ["trimAudioSilence"] = true });

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("audio quality measurement", await refused.Content.ReadAsStringAsync());
        }
        finally { await client.DeleteAsync($"/api/libraries/{id}"); }
    }
}
