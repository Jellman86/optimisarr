using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Tests;

[Collection(TokenedApiCollection.Name)]
public sealed class LibrarySourceEndpointTests(AdminTokenAuthEndpointTests.TokenedApi api)
{
    [PosixFact]
    public async Task Scanning_a_linked_library_root_returns_an_actionable_validation_error()
    {
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        var root = Directory.CreateTempSubdirectory("optimisarr-linked-root-").FullName;
        var alias = root + "-alias";
        Directory.CreateSymbolicLink(alias, root);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var library = new Library { Name = "Linked root", Path = alias };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        try
        {
            using var response = await client.PostAsync($"/api/libraries/{library.Id}/scan", null);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("library.pathUnsafe", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await db.Libraries.Where(row => row.Id == library.Id).ExecuteDeleteAsync();
            Directory.Delete(alias);
            Directory.Delete(root, recursive: true);
        }
    }

    [PosixFact]
    public async Task Content_refuses_leaf_links_linked_parents_and_paths_outside_the_saved_library()
    {
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        var root = Directory.CreateTempSubdirectory("optimisarr-content-").FullName;
        var libraryPath = Directory.CreateDirectory(Path.Combine(root, "library")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        var target = Path.Combine(outside, "movie.mp4");
        await File.WriteAllTextAsync(target, "outside bytes");
        var leaf = Path.Combine(libraryPath, "link.mp4");
        File.CreateSymbolicLink(leaf, target);
        var alias = Path.Combine(libraryPath, "alias");
        Directory.CreateSymbolicLink(alias, outside);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var library = new Library { Name = "Content boundary", Path = libraryPath };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        try
        {
            foreach (var path in new[] { leaf, Path.Combine(alias, "movie.mp4"), target })
            {
                var media = new MediaFile { LibraryId = library.Id, Path = path, RelativePath = "movie.mp4" };
                db.MediaFiles.Add(media);
                await db.SaveChangesAsync();
                using var response = await client.GetAsync($"/api/media/{media.Id}/content");
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.DoesNotContain("outside bytes", await response.Content.ReadAsStringAsync());
            }

            var regular = Path.Combine(libraryPath, "regular.mp4");
            await File.WriteAllTextAsync(regular, "ordinary bytes");
            var allowed = new MediaFile { LibraryId = library.Id, Path = regular, RelativePath = "regular.mp4" };
            db.MediaFiles.Add(allowed);
            await db.SaveChangesAsync();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/media/{allowed.Id}/content");
            request.Headers.Range = new(0, 7);
            using var ranged = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
            Assert.Equal("ordinary", await ranged.Content.ReadAsStringAsync());
        }
        finally
        {
            await db.Libraries.Where(row => row.Id == library.Id).ExecuteDeleteAsync();
            Directory.Delete(root, recursive: true);
        }
    }
}
