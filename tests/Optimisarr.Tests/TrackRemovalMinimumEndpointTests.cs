using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Optimisarr.Tests;

/// <summary>A library's minimum track-removal saving is saved, returned, cleared and validated by the library API.</summary>
[Collection(TokenedApiCollection.Name)]
public sealed class TrackRemovalMinimumEndpointTests(AdminTokenAuthEndpointTests.TokenedApi api)
{
    [Fact]
    public async Task A_minimum_track_removal_saving_round_trips_and_can_be_cleared()
    {
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        var path = Directory.CreateDirectory(Path.Combine(api.LibraryDirectory, "cleanup-" + Guid.NewGuid().ToString("N"))).FullName;
        var body = new Dictionary<string, object?>
        {
            ["name"] = "Cleanup films", ["path"] = path, ["mediaType"] = "Film", ["ruleProfile"] = "TrackCleanup",
            ["keepAudioLanguages"] = "eng",
        };
        using var plain = await client.PostAsJsonAsync("/api/libraries", body);
        Assert.Equal(HttpStatusCode.Created, plain.StatusCode);
        var created = JsonNode.Parse(await plain.Content.ReadAsStringAsync())!;
        var id = created["id"]!.GetValue<int>();
        try
        {
            Assert.Null(created["minimumTrackRemovalSavingPercent"]);
            Assert.Null(created["minimumTrackRemovalSavingBytes"]);

            using var set = await client.PutAsJsonAsync($"/api/libraries/{id}", new Dictionary<string, object?>(body)
            {
                ["minimumTrackRemovalSavingPercent"] = 5, ["minimumTrackRemovalSavingBytes"] = 1_073_741_824L,
            });
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            var saved = JsonNode.Parse(await set.Content.ReadAsStringAsync())!;
            Assert.Equal(5, saved["minimumTrackRemovalSavingPercent"]!.GetValue<double>());
            Assert.Equal(1_073_741_824L, saved["minimumTrackRemovalSavingBytes"]!.GetValue<long>());

            var listed = JsonNode.Parse(await client.GetStringAsync("/api/libraries"))!.AsArray()
                .Single(library => library!["id"]!.GetValue<int>() == id)!;
            Assert.Equal(5, listed["minimumTrackRemovalSavingPercent"]!.GetValue<double>());

            using var cleared = await client.PutAsJsonAsync($"/api/libraries/{id}", body);
            var none = JsonNode.Parse(await cleared.Content.ReadAsStringAsync())!;
            Assert.Null(none["minimumTrackRemovalSavingPercent"]);
            Assert.Null(none["minimumTrackRemovalSavingBytes"]);

            using var refused = await client.PutAsJsonAsync($"/api/libraries/{id}", new Dictionary<string, object?>(body)
            {
                ["minimumTrackRemovalSavingPercent"] = 0,
            });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("minimum track-removal saving", await refused.Content.ReadAsStringAsync());
        }
        finally { await client.DeleteAsync($"/api/libraries/{id}"); }
    }
}
