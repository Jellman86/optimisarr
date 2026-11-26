using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Tests;

/// <summary>The Inventory page sorts and searches through query parameters, so the endpoint must
/// honour each documented sort and refuse an unknown one instead of silently returning path order.</summary>
[Collection(TokenedApiCollection.Name)]
public sealed class InventoryEndpointTests(AdminTokenAuthEndpointTests.TokenedApi api)
{
    [Fact]
    public async Task Inventory_orders_a_page_by_the_requested_sort()
    {
        var libraryId = await SeedAsync(("small.mkv", 1_000), ("large.mkv", 9_000), ("medium.mkv", 5_000));
        using var client = Admin();

        var bySize = await client.GetFromJsonAsync<JsonElement>($"/api/inventory?libraryId={libraryId}&sort=size_desc");
        var byName = await client.GetFromJsonAsync<JsonElement>($"/api/inventory?libraryId={libraryId}&sort=NAME_DESC&search=a");

        Assert.Equal(["large.mkv", "medium.mkv", "small.mkv"], Paths(bySize));
        Assert.Equal(["small.mkv", "large.mkv"], Paths(byName));
    }

    [Fact]
    public async Task Inventory_rejects_an_unknown_sort_with_a_coded_bad_request()
    {
        using var client = Admin();

        var response = await client.GetAsync("/api/inventory?sort=biggest");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("inventory.sort.invalid", error.GetProperty("code").GetString());
        Assert.Equal("biggest", error.GetProperty("args").GetProperty("value").GetString());
    }

    private static string[] Paths(JsonElement page) =>
        page.GetProperty("items").EnumerateArray()
            .Select(row => row.GetProperty("file").GetProperty("relativePath").GetString() ?? "")
            .ToArray();

    private HttpClient Admin()
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        return client;
    }

    private async Task<int> SeedAsync(params (string RelativePath, long SizeBytes)[] files)
    {
        // The fixture is shared: a library whose folder is missing would hold back the setup tests.
        var root = Path.Combine(api.LibraryDirectory, "inventory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var library = new Library { Name = "Inventory " + Guid.NewGuid().ToString("N"), Path = root, AutoEnqueueEnabled = false };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        foreach (var (relativePath, sizeBytes) in files)
        {
            db.MediaFiles.Add(new MediaFile
            {
                LibraryId = library.Id, Path = Path.Combine(root, relativePath), RelativePath = relativePath,
                SizeBytes = sizeBytes, Status = MediaFileStatus.Discovered
            });
        }
        await db.SaveChangesAsync();
        return library.Id;
    }
}
