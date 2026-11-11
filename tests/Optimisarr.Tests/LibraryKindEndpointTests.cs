using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Data;

namespace Optimisarr.Tests;

/// <summary>
/// What setup and the editor send for each kind of library has to be something the server takes.
/// A music or photo library has no video, so it is saved with a fixed quality strategy and no
/// picture check, and the server refuses one that asks for either.
/// </summary>
[Collection(TokenedApiCollection.Name)]
public sealed class LibraryKindEndpointTests(AdminTokenAuthEndpointTests.TokenedApi api) : IAsyncLifetime
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "optimisarr-library-kind-" + Guid.NewGuid().ToString("N"))).FullName;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await db.Libraries.Where(library => library.Path.StartsWith(_root)).ExecuteDeleteAsync();
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("Music")]
    [InlineData("Photo")]
    public async Task A_library_with_no_video_saves_with_a_fixed_strategy_and_no_picture_check(string mediaType)
    {
        using var client = Admin();

        var saved = await client.PostAsJsonAsync("/api/libraries", Fields(mediaType, "Fixed", pictureCheck: false));

        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
    }

    [Theory]
    [InlineData("Music")]
    [InlineData("Photo")]
    public async Task A_library_with_no_video_is_refused_when_it_asks_for_a_picture_score(string mediaType)
    {
        using var client = Admin();

        var refused = await client.PostAsJsonAsync("/api/libraries", Fields(mediaType, "AdaptiveVmaf", pictureCheck: true));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("library.validation", await refused.Content.ReadAsStringAsync());
    }

    private object Fields(string mediaType, string strategy, bool pictureCheck) => new
    {
        name = $"{mediaType} {Guid.NewGuid():N}",
        path = Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N"))).FullName,
        mediaType,
        ruleProfile = "ConservativeHevc",
        enabled = false,
        videoQualityStrategy = strategy,
        vmafQualityGateEnabled = pictureCheck,
        minVmafHarmonicMean = pictureCheck ? 93 : (double?)null,
        minVmafMin = pictureCheck ? 80 : (double?)null,
        minVmafCatastrophicMin = pictureCheck ? 50 : (double?)null,
    };

    private HttpClient Admin()
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        return client;
    }
}
