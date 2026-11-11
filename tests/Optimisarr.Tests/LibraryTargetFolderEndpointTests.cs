using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Optimisarr.Api.Library;
using Optimisarr.Api.Queue;
using Optimisarr.Api.Replacement;
using Optimisarr.Core.Library;
using Optimisarr.Data;

namespace Optimisarr.Tests;

/// <summary>
/// "Save optimised files in another folder" must never point inside a library, the working space
/// or Quarantine. These run against the real API, so the rule is proved where a save meets it.
/// </summary>
[Collection(TokenedApiCollection.Name)]
public sealed class LibraryTargetFolderEndpointTests(AdminTokenAuthEndpointTests.TokenedApi api) : IAsyncLifetime
{
    // Outside the shared fixture's library folder: other tests register that whole folder as a library.
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "optimisarr-target-endpoint-" + Guid.NewGuid().ToString("N"))).FullName;

    public Task InitializeAsync() => Task.CompletedTask;

    // The fixture's database is shared, and other tests expect every saved library's folder to exist.
    public async Task DisposeAsync()
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await db.Libraries.Where(library => library.Path.StartsWith(_root)).ExecuteDeleteAsync();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Saving_refuses_a_target_folder_inside_the_library_being_saved()
    {
        using var client = Admin();
        var library = Folder("tv");

        var response = await client.PostAsJsonAsync("/api/libraries", Fields("Own folder", library, Folder("tv", "optimised")));

        await AssertRefusedAsync(response, "library.targetFolder.insideLibrary", "Own folder");
    }

    [Fact]
    public async Task Saving_refuses_a_target_folder_inside_another_library_and_names_it()
    {
        using var client = Admin();
        var other = "Other " + Guid.NewGuid().ToString("N");
        (await client.PostAsJsonAsync("/api/libraries", Fields(other, Folder("films")))).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/api/libraries", Fields("Saver", Folder("tv"), Folder("films", "from-tv")));

        await AssertRefusedAsync(response, "library.targetFolder.insideLibrary", other);
    }

    [Fact]
    public async Task Saving_refuses_the_working_space_and_quarantine()
    {
        using var client = Admin();
        var environment = api.Services.GetRequiredService<IHostEnvironment>();
        var library = Folder("tv");

        var work = await client.PostAsJsonAsync("/api/libraries",
            Fields("Work saver", library, Path.Combine(WorkPaths.Resolve(environment), "kept")));
        var quarantine = await client.PostAsJsonAsync("/api/libraries",
            Fields("Quarantine saver", library, TrashPaths.Resolve(environment)));

        await AssertRefusedAsync(work, "library.targetFolder.workFolder");
        await AssertRefusedAsync(quarantine, "library.targetFolder.quarantine");
    }

    [Fact]
    public async Task Saving_refuses_a_target_folder_that_does_not_exist()
    {
        using var client = Admin();

        var response = await client.PostAsJsonAsync("/api/libraries",
            Fields("Missing saver", Folder("tv"), Path.Combine(_root, "not-made")));

        await AssertRefusedAsync(response, "library.targetFolder.missing");
    }

    [Fact]
    public async Task An_update_that_moves_the_library_onto_its_own_target_folder_is_refused_and_changes_nothing()
    {
        using var client = Admin();
        var name = "Mover " + Guid.NewGuid().ToString("N");
        var library = Folder("tv");
        var target = Folder("optimised");
        var created = await client.PostAsJsonAsync("/api/libraries", Fields(name, library, target));
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var moved = await client.PutAsJsonAsync($"/api/libraries/{id}", Fields(name, target, target));

        await AssertRefusedAsync(moved, "library.targetFolder.insideLibrary", name);
        var saved = (await client.GetFromJsonAsync<JsonElement>("/api/libraries")).EnumerateArray()
            .Single(row => row.GetProperty("id").GetInt32() == id);
        Assert.Equal(library, saved.GetProperty("path").GetString());
    }

    [Fact]
    public async Task A_folder_outside_every_library_saves_and_replace_automatically_is_stored_off()
    {
        using var client = Admin();
        var target = Folder("optimised");

        var created = await client.PostAsJsonAsync("/api/libraries", Fields("Saver " + Guid.NewGuid().ToString("N"), Folder("tv"), target, autoReplace: true));

        created.EnsureSuccessStatusCode();
        var dto = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(dto.GetProperty("moveOnComplete").GetBoolean());
        Assert.Equal(target, dto.GetProperty("targetFolder").GetString());
        Assert.False(dto.GetProperty("autoReplace").GetBoolean());
    }

    [Fact]
    public async Task The_check_endpoint_says_why_a_folder_is_refused_and_how_much_room_an_allowed_one_has_where_that_is_known()
    {
        using var client = Admin();
        var name = "Checked " + Guid.NewGuid().ToString("N");
        (await client.PostAsJsonAsync("/api/libraries", Fields(name, Folder("tv")))).EnsureSuccessStatusCode();

        var inside = await CheckAsync(client, Folder("tv", "optimised"));
        var outside = await CheckAsync(client, Folder("optimised"));
        var missing = await CheckAsync(client, Path.Combine(_root, "not-made"));

        Assert.False(inside.GetProperty("allowed").GetBoolean());
        Assert.Equal("insideLibrary", inside.GetProperty("reason").GetString());
        Assert.Equal(name, inside.GetProperty("libraryName").GetString());
        Assert.True(outside.GetProperty("allowed").GetBoolean());
        Assert.Equal("none", outside.GetProperty("reason").GetString());
        // Free space comes from mount evidence, which not every platform can give.
        var available = outside.GetProperty("availableBytes");
        Assert.True(available.ValueKind == JsonValueKind.Null || available.GetInt64() > 0);
        Assert.Equal("missing", missing.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_finished_file_is_refused_when_a_library_was_added_over_its_destination_after_the_save()
    {
        using var client = Admin();
        var target = Folder("optimised");
        (await client.PostAsJsonAsync("/api/libraries", Fields("Saver " + Guid.NewGuid().ToString("N"), Folder("tv"), target))).EnsureSuccessStatusCode();
        var destination = Path.Combine(target, "Show", "Season 01", "Episode.mkv");
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var environment = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
        Assert.True((await TargetFolderGuard.CheckDestinationAsync(destination, db, environment, CancellationToken.None)).IsAllowed);

        var later = "Later " + Guid.NewGuid().ToString("N");
        (await client.PostAsJsonAsync("/api/libraries", Fields(later, target))).EnsureSuccessStatusCode();
        var verdict = await TargetFolderGuard.CheckDestinationAsync(destination, db, environment, CancellationToken.None);

        Assert.Equal(TargetFolderRefusal.InsideLibrary, verdict.Refusal);
        Assert.Equal(later, verdict.LibraryName);
    }

    [Fact]
    public async Task The_check_endpoint_judges_an_unsaved_library_by_the_folder_it_is_about_to_have()
    {
        using var client = Admin();
        var name = "Moving " + Guid.NewGuid().ToString("N");
        var created = await client.PostAsJsonAsync("/api/libraries", Fields(name, Folder("tv")));
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var old = Folder("tv", "optimised");
        var next = Folder("shows", "optimised");

        var unsaved = await CheckAsync(client, new { path = Folder("films", "optimised"), libraryName = "New", libraryPath = Folder("films") });
        var movedAway = await CheckAsync(client, new { path = old, libraryId = id, libraryName = name, libraryPath = Folder("shows") });
        var movedOnto = await CheckAsync(client, new { path = next, libraryId = id, libraryName = name, libraryPath = Folder("shows") });

        Assert.Equal("New", unsaved.GetProperty("libraryName").GetString());
        Assert.True(movedAway.GetProperty("allowed").GetBoolean());
        Assert.Equal("insideLibrary", movedOnto.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task The_check_endpoint_needs_a_folder()
    {
        using var client = Admin();

        var response = await client.PostAsJsonAsync("/api/libraries/target-folder-check", new { path = " " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_folder_that_cannot_be_a_path_is_bad_input_not_a_server_error()
    {
        using var client = Admin();
        var broken = Path.Combine(_root, "opti\0mised");

        var checkedFolder = await client.PostAsJsonAsync("/api/libraries/target-folder-check", new { path = broken });
        var checkedLibrary = await client.PostAsJsonAsync("/api/libraries/target-folder-check",
            new { path = Folder("optimised"), libraryPath = broken, libraryName = "Broken" });
        var saved = await client.PostAsJsonAsync("/api/libraries", Fields("Broken saver", Folder("tv"), broken));

        foreach (var response in new[] { checkedFolder, checkedLibrary, saved })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("library.validation", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task A_refused_destination_writes_nothing_and_keeps_the_output_in_the_work_folder()
    {
        using var client = Admin();
        var target = Folder("optimised");
        (await client.PostAsJsonAsync("/api/libraries", Fields("Late " + Guid.NewGuid().ToString("N"), target))).EnsureSuccessStatusCode();
        var (workRoot, output) = WorkOutput("Show", "Episode.mkv");

        var saved = await Saver().SaveAsync(workRoot, output, target, overwrite: true);

        Assert.Null(saved.Destination);
        Assert.Contains("The optimised file was not saved.", saved.Error);
        Assert.Contains("Your original has not been touched.", saved.Error);
        Assert.Equal("VERIFIED", await File.ReadAllTextAsync(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public async Task An_allowed_destination_receives_the_output_under_the_same_folders()
    {
        var target = Folder("optimised");
        var (workRoot, output) = WorkOutput("Show", "Episode.mkv");

        var saved = await Saver().SaveAsync(workRoot, output, target, overwrite: false);

        Assert.Null(saved.Error);
        Assert.Equal(Path.Combine(target, "Show", "Episode.mkv"), Path.GetFullPath(saved.Destination!));
        Assert.Equal("VERIFIED", await File.ReadAllTextAsync(saved.Destination!));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task A_file_already_at_the_destination_is_kept_when_overwrite_is_off()
    {
        var target = Folder("optimised");
        var existing = Path.Combine(Folder("optimised", "Show"), "Episode.mkv");
        await File.WriteAllTextAsync(existing, "EARLIER");
        var (workRoot, output) = WorkOutput("Show", "Episode.mkv");

        var saved = await Saver().SaveAsync(workRoot, output, target, overwrite: false);

        Assert.Contains("already exists at the destination", saved.Error);
        Assert.Equal("EARLIER", await File.ReadAllTextAsync(existing));
        Assert.Equal("VERIFIED", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task A_saved_library_whose_folder_now_breaks_the_rule_is_refused_before_encoding()
    {
        using var client = Admin();
        var target = Folder("optimised");
        var allowed = await Saver().RefusalBeforeEncodingAsync(target, CancellationToken.None);
        var later = "Later " + Guid.NewGuid().ToString("N");
        (await client.PostAsJsonAsync("/api/libraries", Fields(later, target))).EnsureSuccessStatusCode();

        var refused = await Saver().RefusalBeforeEncodingAsync(target, CancellationToken.None);

        Assert.Null(allowed);
        Assert.StartsWith("Not encoded.", refused);
        Assert.Contains($"inside the {later} library", refused);
        Assert.Contains("Your original has not been touched.", refused);
    }

    private SaveElsewhere Saver() =>
        new(api.Services.GetRequiredService<IServiceScopeFactory>(), api.Services.GetRequiredService<IHostEnvironment>());

    // A verified output as the dispatcher leaves it: under the file's own work root, in the library's folders.
    private (string WorkRoot, string Output) WorkOutput(string folder, string name)
    {
        var workRoot = Folder("work-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(Directory.CreateDirectory(Path.Combine(workRoot, folder)).FullName, name);
        File.WriteAllText(output, "VERIFIED");
        return (workRoot, output);
    }

    private static Task<JsonElement> CheckAsync(HttpClient client, string path) => CheckAsync(client, new { path });

    private static async Task<JsonElement> CheckAsync(HttpClient client, object request)
    {
        var response = await client.PostAsJsonAsync("/api/libraries/target-folder-check", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string code, string? library = null)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, error.GetProperty("code").GetString());
        if (library is not null)
        {
            Assert.Equal(library, error.GetProperty("args").GetProperty("library").GetString());
        }
    }

    private static object Fields(string name, string path, string? targetFolder = null, bool autoReplace = false) => new
    {
        name,
        path,
        mediaType = "Tv",
        ruleProfile = "ConservativeHevc",
        enabled = false,
        moveOnComplete = targetFolder is not null,
        targetFolder,
        autoReplace,
    };

    private string Folder(params string[] parts) => Directory.CreateDirectory(Path.Combine([_root, .. parts])).FullName;

    private HttpClient Admin()
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        return client;
    }
}
