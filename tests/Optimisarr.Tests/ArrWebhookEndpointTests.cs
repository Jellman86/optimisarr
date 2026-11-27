using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Api.Library;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Tests;

/// <summary>
/// The Sonarr/Radarr webhook end to end, against the real API with the admin token set. Payloads are
/// fabricated in the managers' shape. The connections point at a closed local port so the import-hold
/// check, which asks every enabled manager about its queue, fails fast and holds nothing.
/// </summary>
[Collection(TokenedApiCollection.Name)]
public sealed class ArrWebhookEndpointTests(AdminTokenAuthEndpointTests.TokenedApi api) : IAsyncLifetime
{
    private const string UnreachableManager = "http://127.0.0.1:9";

    private readonly List<int> _libraries = [];

    public Task InitializeAsync() => Task.CompletedTask;

    // Other tests in this host count every queued job, so each test removes its libraries, and with them
    // the files and jobs it made.
    public async Task DisposeAsync()
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await db.Jobs.Where(job => job.LibraryId != null && _libraries.Contains(job.LibraryId.Value)).ExecuteDeleteAsync();
        await db.MediaFiles.Where(file => file.LibraryId != null && _libraries.Contains(file.LibraryId.Value)).ExecuteDeleteAsync();
        await db.Libraries.Where(library => _libraries.Contains(library.Id)).ExecuteDeleteAsync();
    }

    private HttpClient Admin()
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminTokenAuthEndpointTests.TokenedApi.Token);
        return client;
    }

    private sealed record Webhook(int ConnectionId, string Path, string Password) : IAsyncDisposable
    {
        public required AdminTokenAuthEndpointTests.TokenedApi Api { get; init; }

        public async ValueTask DisposeAsync()
        {
            using var scope = Api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            await db.ArrConnections.Where(c => c.Id == ConnectionId).ExecuteDeleteAsync();
        }
    }

    private async Task<Webhook> CreateWebhookAsync(string type = "Sonarr", bool enabled = true, string? remote = null, string? local = null)
    {
        using var client = Admin();
        var created = await client.PostAsJsonAsync("/api/arr-connections", new
        {
            name = "Webhook " + Guid.NewGuid().ToString("N"),
            type,
            baseUrl = UnreachableManager,
            enabled,
            remotePathPrefix = remote,
            localPathPrefix = local,
        });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var issued = await client.PostAsync($"/api/arr-connections/{id}/webhook-password", null);
        issued.EnsureSuccessStatusCode();
        var body = await issued.Content.ReadFromJsonAsync<JsonElement>();
        return new Webhook(id, body.GetProperty("webhookPath").GetString()!, body.GetProperty("password").GetString()!) { Api = api };
    }

    private async Task<(Library Library, string Root)> SeedLibraryAsync(bool autoEnqueue, TimeOnly? windowStart = null, TimeOnly? windowEnd = null)
    {
        var root = Path.Combine(api.LibraryDirectory, "webhook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var library = new Library
        {
            Name = "Webhook TV", Path = root, MediaType = MediaType.Tv, Priority = 2, MinFileSizeBytes = 0, SkipEfficientSources = false,
            AutoEnqueueEnabled = autoEnqueue,
            AutoEnqueueWindowStart = windowStart ?? new TimeOnly(0, 0),
            AutoEnqueueWindowEnd = windowEnd ?? new TimeOnly(0, 0),
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        _libraries.Add(library.Id);
        return (library, root);
    }

    private static string WriteEpisode(string root, string relative, TimeSpan age)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[2048]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    private static string SonarrImport(string path, bool isUpgrade = false) => JsonSerializer.Serialize(new
    {
        eventType = "Download",
        series = new { id = 1, title = "Fabricated Show", path = Path.GetDirectoryName(path) },
        episodeFile = new { id = 10, relativePath = Path.GetFileName(path), path },
        isUpgrade,
    });

    private async Task<HttpResponseMessage> DeliverAsync(Webhook hook, string json, string? password = null, string username = "optimisarr")
    {
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, hook.Path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password ?? hook.Password}")));
        return await client.SendAsync(request);
    }

    private async Task<T> InDbAsync<T>(Func<OptimisarrDbContext, Task<T>> read)
    {
        using var scope = api.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>());
    }

    private async Task HoldQueueAsync(Func<Task> action)
    {
        using var client = Admin();
        (await client.PostAsync("/api/queue/pause", null)).EnsureSuccessStatusCode();
        try { await action(); }
        finally { (await client.PostAsync("/api/queue/resume", null)).EnsureSuccessStatusCode(); }
    }

    private static async Task<string> OutcomeOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("outcome").GetString()!;

    [Fact]
    public async Task The_managers_test_button_succeeds_without_the_admin_token_and_is_recorded()
    {
        await using var hook = await CreateWebhookAsync();

        using var response = await DeliverAsync(hook, """{ "eventType": "Test", "series": { "id": 1, "title": "Test Title", "path": "C:\\testpath" } }""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Test", await OutcomeOf(response));
        var connection = await InDbAsync(db => db.ArrConnections.AsNoTracking().SingleAsync(c => c.Id == hook.ConnectionId));
        Assert.NotNull(connection.WebhookLastReceivedAt);
        Assert.Equal(ArrWebhookOutcome.Test, connection.WebhookLastOutcome);
    }

    [Theory]
    [InlineData("wrong-password")]
    [InlineData("")]
    public async Task A_wrong_password_is_refused_and_records_nothing(string password)
    {
        await using var hook = await CreateWebhookAsync();

        using var response = await DeliverAsync(hook, """{ "eventType": "Test" }""", password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Basic", response.Headers.WwwAuthenticate.ToString());
        Assert.Null(await InDbAsync(db => db.ArrConnections.Where(c => c.Id == hook.ConnectionId).Select(c => c.WebhookLastReceivedAt).SingleAsync()));
    }

    [Fact]
    public async Task No_credentials_or_the_admin_token_are_refused()
    {
        await using var hook = await CreateWebhookAsync();
        using var anonymous = api.CreateClient();
        using var admin = Admin();

        using var bare = await anonymous.PostAsync(hook.Path, new StringContent("""{ "eventType": "Test" }""", Encoding.UTF8, "application/json"));
        using var bearer = await admin.PostAsync(hook.Path, new StringContent("""{ "eventType": "Test" }""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, bare.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, bearer.StatusCode);
    }

    [Fact]
    public async Task Another_connections_password_and_an_unknown_connection_are_refused()
    {
        await using var first = await CreateWebhookAsync();
        await using var second = await CreateWebhookAsync();

        using var crossed = await DeliverAsync(first, """{ "eventType": "Test" }""", second.Password);
        using var unknown = await DeliverAsync(first with { Path = "/api/arr-webhooks/987654", Api = api }, """{ "eventType": "Test" }""");

        Assert.Equal(HttpStatusCode.Unauthorized, crossed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
    }

    [Fact]
    public async Task A_new_password_replaces_the_old_one_and_turning_the_webhook_off_refuses_both()
    {
        await using var hook = await CreateWebhookAsync();
        using var client = Admin();

        var reissued = await (await client.PostAsync($"/api/arr-connections/{hook.ConnectionId}/webhook-password", null))
            .Content.ReadFromJsonAsync<JsonElement>();
        var newPassword = reissued.GetProperty("password").GetString()!;

        Assert.Equal(HttpStatusCode.Unauthorized, (await DeliverAsync(hook, """{ "eventType": "Test" }""")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DeliverAsync(hook, """{ "eventType": "Test" }""", newPassword)).StatusCode);

        (await client.DeleteAsync($"/api/arr-connections/{hook.ConnectionId}/webhook-password")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await DeliverAsync(hook, """{ "eventType": "Test" }""", newPassword)).StatusCode);
    }

    [Fact]
    public async Task The_connection_list_says_a_webhook_is_on_but_never_returns_its_password()
    {
        await using var hook = await CreateWebhookAsync();
        using var client = Admin();

        var listed = await client.GetStringAsync("/api/arr-connections");

        Assert.DoesNotContain(hook.Password, listed);
        var connection = JsonDocument.Parse(listed).RootElement.EnumerateArray().Single(c => c.GetProperty("id").GetInt32() == hook.ConnectionId);
        Assert.True(connection.GetProperty("webhookEnabled").GetBoolean());
        Assert.Equal(hook.Path, connection.GetProperty("webhookPath").GetString());
        var stored = await InDbAsync(db => db.ArrConnections.Where(c => c.Id == hook.ConnectionId).Select(c => c.WebhookSecretFingerprint).SingleAsync());
        Assert.NotEqual(hook.Password, stored);
    }

    [Theory]
    [InlineData("Grab")]
    [InlineData("Rename")]
    [InlineData("Health")]
    public async Task Events_optimisarr_does_not_use_succeed_so_the_manager_keeps_the_connection_healthy(string eventType)
    {
        await using var hook = await CreateWebhookAsync();

        using var response = await DeliverAsync(hook, $$"""{ "eventType": "{{eventType}}", "series": { "path": "/tv/Show" } }""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Ignored", await OutcomeOf(response));
    }

    [Fact]
    public async Task A_malformed_body_is_a_bad_request()
    {
        await using var hook = await CreateWebhookAsync();

        using var response = await DeliverAsync(hook, "this is not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_oversized_body_is_refused_before_it_is_read()
    {
        await using var hook = await CreateWebhookAsync();

        using var response = await DeliverAsync(hook, new string(' ', 1024 * 1024 + 1));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task An_imported_file_is_probed_and_queued_when_the_library_optimises_automatically() => await HoldQueueAsync(async () =>
    {
        var (library, root) = await SeedLibraryAsync(autoEnqueue: true);
        var episode = WriteEpisode(root, "Show/Season 01/S01E01.mkv", TimeSpan.FromHours(1));
        await using var hook = await CreateWebhookAsync();

        using var response = await DeliverAsync(hook, SonarrImport(episode));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Queued", await OutcomeOf(response));
        var file = await InDbAsync(db => db.MediaFiles.AsNoTracking().SingleAsync(f => f.Path == episode));
        Assert.Equal(MediaFileStatus.Probed, file.Status);
        Assert.Equal(library.Id, file.LibraryId);
        var job = await InDbAsync(db => db.Jobs.AsNoTracking().SingleAsync(j => j.MediaFileId == file.Id));
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(2, job.Priority);
        Assert.NotNull(await InDbAsync(db => db.Libraries.Where(l => l.Id == library.Id).Select(l => l.LastAutoEnqueueAt).SingleAsync()));

        using var again = await DeliverAsync(hook, SonarrImport(episode, isUpgrade: true));
        Assert.Equal("NotQueued", await OutcomeOf(again));
        Assert.Single(await InDbAsync(db => db.Jobs.AsNoTracking().Where(j => j.MediaFileId == file.Id).ToListAsync()));
    });

    [Fact]
    public async Task A_library_that_does_not_optimise_automatically_takes_the_file_in_without_queueing_it()
    {
        var (_, root) = await SeedLibraryAsync(autoEnqueue: false);
        var episode = WriteEpisode(root, "Show/S01E01.mkv", TimeSpan.FromHours(1));
        await using var hook = await CreateWebhookAsync();

        using var response = await DeliverAsync(hook, SonarrImport(episode));

        Assert.Equal("NotQueued", await OutcomeOf(response));
        var file = await InDbAsync(db => db.MediaFiles.AsNoTracking().SingleAsync(f => f.Path == episode));
        Assert.Equal(MediaFileStatus.Probed, file.Status);
        Assert.Empty(await InDbAsync(db => db.Jobs.AsNoTracking().Where(j => j.MediaFileId == file.Id).ToListAsync()));
    }

    [Fact]
    public async Task A_library_outside_its_schedule_is_not_queued_until_the_schedule_opens()
    {
        var now = TimeOnly.FromDateTime(DateTime.Now);
        var (_, root) = await SeedLibraryAsync(autoEnqueue: true, now.AddHours(2), now.AddHours(3));
        var episode = WriteEpisode(root, "Show/S01E01.mkv", TimeSpan.FromHours(1));
        await using var hook = await CreateWebhookAsync();

        using var response = await DeliverAsync(hook, SonarrImport(episode));

        Assert.Equal("NotQueued", await OutcomeOf(response));
        var file = await InDbAsync(db => db.MediaFiles.AsNoTracking().SingleAsync(f => f.Path == episode));
        Assert.Empty(await InDbAsync(db => db.Jobs.AsNoTracking().Where(j => j.MediaFileId == file.Id).ToListAsync()));
    }

    [Fact]
    public async Task A_file_the_rules_skip_is_taken_in_but_not_queued() => await HoldQueueAsync(async () =>
    {
        var (_, root) = await SeedLibraryAsync(autoEnqueue: true);
        // The test probe reports a "ten-bit" file as HEVC, which the default rules leave alone.
        var episode = WriteEpisode(root, "Show/S01E01 ten-bit.mkv", TimeSpan.FromHours(1));
        await using var hook = await CreateWebhookAsync();

        using var response = await DeliverAsync(hook, SonarrImport(episode));

        Assert.Equal("NotQueued", await OutcomeOf(response));
        var file = await InDbAsync(db => db.MediaFiles.AsNoTracking().SingleAsync(f => f.Path == episode));
        Assert.Empty(await InDbAsync(db => db.Jobs.AsNoTracking().Where(j => j.MediaFileId == file.Id).ToListAsync()));
    });

    [Fact]
    public async Task A_file_still_being_written_is_looked_at_again_once_it_settles() => await HoldQueueAsync(async () =>
    {
        var (_, root) = await SeedLibraryAsync(autoEnqueue: true);
        var episode = WriteEpisode(root, "Show/S01E02.mkv", TimeSpan.Zero);
        await using var hook = await CreateWebhookAsync();

        using var response = await DeliverAsync(hook, SonarrImport(episode));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Settling", await OutcomeOf(response));
        Assert.False(await InDbAsync(db => db.MediaFiles.AnyAsync(f => f.Path == episode)));

        var settling = api.Services.GetRequiredService<ArrWebhookSettlingQueue>();
        Assert.Empty(settling.TakeDue(DateTimeOffset.UtcNow));
        var due = Assert.Single(settling.TakeDue(DateTimeOffset.UtcNow.AddMinutes(5)), file => file.ConnectionId == hook.ConnectionId);
        File.SetLastWriteTimeUtc(episode, DateTime.UtcNow.AddHours(-1));
        using (var scope = api.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ArrWebhookService>().RevisitAsync(due, CancellationToken.None);
        }

        var file = await InDbAsync(db => db.MediaFiles.AsNoTracking().SingleAsync(f => f.Path == episode));
        Assert.Single(await InDbAsync(db => db.Jobs.AsNoTracking().Where(j => j.MediaFileId == file.Id).ToListAsync()));
        Assert.Equal(ArrWebhookOutcome.Queued,
            await InDbAsync(db => db.ArrConnections.Where(c => c.Id == hook.ConnectionId).Select(c => c.WebhookLastOutcome).SingleAsync()));
    });

    [Fact]
    public async Task A_file_outside_every_library_succeeds_as_not_in_a_library()
    {
        using var outside = new TemporaryFolder();
        var stray = WriteEpisode(outside.Path, "Show/S01E01.mkv", TimeSpan.FromHours(1));
        await using var hook = await CreateWebhookAsync();

        using var response = await DeliverAsync(hook, SonarrImport(stray));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("NotInLibrary", await OutcomeOf(response));
        Assert.False(await InDbAsync(db => db.MediaFiles.AnyAsync(f => f.Path == stray)));
    }

    [Fact]
    public async Task A_path_that_climbs_out_of_a_library_is_refused_and_takes_nothing_in()
    {
        var (_, root) = await SeedLibraryAsync(autoEnqueue: true);
        using var outside = new TemporaryFolder();
        var stray = WriteEpisode(outside.Path, "escape.mkv", TimeSpan.FromHours(1));
        var climbing = root + "/../../" + Path.GetRelativePath(Path.GetDirectoryName(Path.GetDirectoryName(root))!, stray);
        await using var hook = await CreateWebhookAsync();

        using var response = await DeliverAsync(hook, JsonSerializer.Serialize(new
        {
            eventType = "Download",
            episodeFile = new { path = climbing },
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Refused", await OutcomeOf(response));
        Assert.False(await InDbAsync(db => db.MediaFiles.AnyAsync(f => f.Path == stray || f.Path == climbing)));
    }

    [PosixFact]
    public async Task A_file_reached_through_a_link_out_of_the_library_is_refused()
    {
        var (_, root) = await SeedLibraryAsync(autoEnqueue: true);
        using var outside = new TemporaryFolder();
        var real = WriteEpisode(outside.Path, "real.mkv", TimeSpan.FromHours(1));
        var link = Path.Combine(root, "linked.mkv");
        File.CreateSymbolicLink(link, real);
        await using var hook = await CreateWebhookAsync();

        using var response = await DeliverAsync(hook, SonarrImport(link));

        Assert.Equal("Refused", await OutcomeOf(response));
        Assert.False(await InDbAsync(db => db.MediaFiles.AnyAsync(f => f.Path == link)));
    }

    [Fact]
    public async Task A_path_mapping_finds_a_file_the_manager_sees_under_another_folder()
    {
        var (_, root) = await SeedLibraryAsync(autoEnqueue: false);
        var episode = WriteEpisode(root, "Show/S01E01.mkv", TimeSpan.FromHours(1));
        await using var hook = await CreateWebhookAsync(remote: "/tv", local: root);

        using var response = await DeliverAsync(hook, JsonSerializer.Serialize(new
        {
            eventType = "Download",
            series = new { path = "/tv/Show" },
            episodeFile = new { relativePath = "S01E01.mkv", path = "/tv/Show/S01E01.mkv" },
        }));

        Assert.Equal("NotQueued", await OutcomeOf(response));
        Assert.True(await InDbAsync(db => db.MediaFiles.AnyAsync(f => f.Path == episode)));
    }

    [Fact]
    public async Task A_radarr_import_is_taken_in_from_its_movie_file()
    {
        var root = Path.Combine(api.LibraryDirectory, "webhook-films-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var films = new Library { Name = "Webhook Films", Path = root, MediaType = MediaType.Film, AutoEnqueueEnabled = false };
            db.Libraries.Add(films);
            await db.SaveChangesAsync();
            _libraries.Add(films.Id);
        }
        var film = WriteEpisode(root, "Film (2021)/Film (2021).mkv", TimeSpan.FromHours(1));
        await using var hook = await CreateWebhookAsync(type: "Radarr");

        using var response = await DeliverAsync(hook, JsonSerializer.Serialize(new
        {
            eventType = "Download",
            movie = new { id = 1, title = "Film", folderPath = Path.GetDirectoryName(film) },
            movieFile = new { id = 2, relativePath = Path.GetFileName(film) },
            isUpgrade = false,
        }));

        Assert.Equal("NotQueued", await OutcomeOf(response));
        Assert.True(await InDbAsync(db => db.MediaFiles.AnyAsync(f => f.Path == film)));
    }

    [Fact]
    public async Task A_connection_turned_off_in_optimisarr_acts_on_nothing()
    {
        var (_, root) = await SeedLibraryAsync(autoEnqueue: true);
        var episode = WriteEpisode(root, "Show/S01E01.mkv", TimeSpan.FromHours(1));
        await using var hook = await CreateWebhookAsync(enabled: false);

        using var response = await DeliverAsync(hook, SonarrImport(episode));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ConnectionOff", await OutcomeOf(response));
        Assert.False(await InDbAsync(db => db.MediaFiles.AnyAsync(f => f.Path == episode)));
    }

    [Theory]
    [InlineData("/tv", null)]
    [InlineData(null, "/data/tv")]
    [InlineData("tv", "/data/tv")]
    [InlineData("/tv", "data/tv")]
    [InlineData("/tv/../etc", "/data/tv")]
    public async Task A_half_filled_or_climbing_path_mapping_is_refused(string? remote, string? local)
    {
        using var client = Admin();

        using var response = await client.PostAsJsonAsync("/api/arr-connections", new
        {
            name = "Mapping " + Guid.NewGuid().ToString("N"), type = "Sonarr", baseUrl = UnreachableManager, enabled = false,
            remotePathPrefix = remote ?? "", localPathPrefix = local ?? "",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("arr.pathMapping.invalid", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Saving_a_connection_without_the_mapping_fields_keeps_the_saved_mapping()
    {
        await using var hook = await CreateWebhookAsync(remote: "/tv", local: "/data/tv");
        using var client = Admin();

        var updated = await client.PutAsJsonAsync($"/api/arr-connections/{hook.ConnectionId}", new
        {
            name = "Renamed " + Guid.NewGuid().ToString("N"), type = "Sonarr", baseUrl = UnreachableManager, enabled = true,
        });

        var body = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("/tv", body.GetProperty("remotePathPrefix").GetString());
        Assert.Equal("/data/tv", body.GetProperty("localPathPrefix").GetString());

        var cleared = await client.PutAsJsonAsync($"/api/arr-connections/{hook.ConnectionId}", new
        {
            name = "Cleared " + Guid.NewGuid().ToString("N"), type = "Sonarr", baseUrl = UnreachableManager, enabled = true,
            remotePathPrefix = "", localPathPrefix = "",
        });
        Assert.Equal(JsonValueKind.Null, (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("remotePathPrefix").ValueKind);
    }

    [Fact]
    public async Task A_file_whose_library_is_being_scanned_waits_for_the_scan_instead_of_holding_the_delivery() => await HoldQueueAsync(async () =>
    {
        var (library, root) = await SeedLibraryAsync(autoEnqueue: true);
        var first = WriteEpisode(root, "Show/S01E01.mkv", TimeSpan.FromHours(1));
        var second = WriteEpisode(root, "Show/S01E02.mkv", TimeSpan.FromHours(1));
        await using var hook = await CreateWebhookAsync();
        var locks = api.Services.GetRequiredService<LibraryInventoryLocks>();
        var settling = api.Services.GetRequiredService<ArrWebhookSettlingQueue>();

        SettlingWebhookFile[] waiting;
        using (await locks.AcquireAsync(library.Id, CancellationToken.None))
        {
            using var response = await DeliverAsync(hook, JsonSerializer.Serialize(new
            {
                eventType = "Download",
                series = new { path = Path.Combine(root, "Show") },
                episodeFiles = new[] { new { path = first }, new { path = second } },
            }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("WaitingForScan", receipt.GetProperty("outcome").GetString());
            Assert.All(receipt.GetProperty("files").EnumerateArray(),
                file => Assert.Equal("WaitingForScan", file.GetProperty("outcome").GetString()));
            Assert.False(await InDbAsync(db => db.MediaFiles.AnyAsync(f => f.LibraryId == library.Id)));

            waiting = [.. settling.TakeDue(DateTimeOffset.UtcNow.AddMinutes(5)).Where(file => file.ConnectionId == hook.ConnectionId)];
            Assert.Equal([first, second], waiting.Select(file => file.LocalPath).Order());

            // A look again while the scan still holds the library waits the same short while and goes back on the list.
            using (var scope = api.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<ArrWebhookService>().RevisitAsync(waiting[0], CancellationToken.None);
            }
            var requeued = Assert.Single(settling.TakeDue(DateTimeOffset.UtcNow.AddMinutes(5)), file => file.ConnectionId == hook.ConnectionId);
            Assert.Equal(waiting[0].Attempts + 1, requeued.Attempts);
            Assert.Equal(ArrWebhookOutcome.WaitingForScan,
                await InDbAsync(db => db.ArrConnections.Where(c => c.Id == hook.ConnectionId).Select(c => c.WebhookLastOutcome).SingleAsync()));
        }

        foreach (var file in waiting)
        {
            using var scope = api.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ArrWebhookService>().RevisitAsync(file, CancellationToken.None);
        }

        var taken = await InDbAsync(db => db.MediaFiles.AsNoTracking().Where(f => f.LibraryId == library.Id).Select(f => f.Id).ToListAsync());
        Assert.Equal(2, taken.Count);
        Assert.Equal(2, await InDbAsync(db => db.Jobs.CountAsync(j => taken.Contains(j.MediaFileId))));
    });

    [Fact]
    public async Task A_file_named_in_another_letter_case_is_recorded_under_its_name_on_disk()
    {
        var (_, root) = await SeedLibraryAsync(autoEnqueue: false);
        var episode = WriteEpisode(root, "Show/S01E01.mkv", TimeSpan.FromHours(1));
        await using var hook = await CreateWebhookAsync();

        using var response = await DeliverAsync(hook, SonarrImport(Path.Combine(root, "SHOW", "s01e01.MKV")));

        Assert.Equal("NotQueued", await OutcomeOf(response));
        var paths = await InDbAsync(db => db.MediaFiles.AsNoTracking().Where(f => f.Path.StartsWith(root)).Select(f => f.Path).ToListAsync());
        Assert.Equal([episode], paths);
    }

    [Fact]
    public async Task Caller_text_is_cut_and_cleaned_once_for_the_receipt_and_the_saved_outcome()
    {
        await using var hook = await CreateWebhookAsync();
        // Escaped in the JSON, so the parsed event type holds a real line break.
        var eventType = "Grab\\nForged: admin logged in" + new string('x', 5000);

        using var ignored = await DeliverAsync(hook, $$"""{ "eventType": "{{eventType}}" }""");

        var detail = (await ignored.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!;
        Assert.DoesNotContain('\n', detail);
        Assert.True(detail.Length < 200, detail);
        Assert.Equal(detail, await InDbAsync(db => db.ArrConnections.Where(c => c.Id == hook.ConnectionId).Select(c => c.WebhookLastDetail).SingleAsync()));

        var longPath = "/nowhere/" + new string('y', 5000) + "\\r\\n.mkv";
        using var refused = await DeliverAsync(hook, $$"""{ "eventType": "Download", "episodeFile": { "path": "{{longPath}}" } }""");

        var receipt = await refused.Content.ReadFromJsonAsync<JsonElement>();
        var file = Assert.Single(receipt.GetProperty("files").EnumerateArray());
        Assert.True(file.GetProperty("path").GetString()!.Length <= 512);
        Assert.True(file.GetProperty("detail").GetString()!.Length <= 1024);
        Assert.DoesNotContain(file.GetProperty("path").GetString()!, character => char.IsControl(character));
        Assert.DoesNotContain(file.GetProperty("detail").GetString()!, character => char.IsControl(character));
    }

    [Fact]
    public async Task A_connection_removed_while_its_delivery_is_handled_is_ignored_rather_than_failing()
    {
        await using var hook = await CreateWebhookAsync();
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var connection = await db.ArrConnections.SingleAsync(c => c.Id == hook.ConnectionId);
        await InDbAsync(other => other.ArrConnections.Where(c => c.Id == hook.ConnectionId).ExecuteDeleteAsync());

        var result = await scope.ServiceProvider.GetRequiredService<ArrWebhookService>()
            .HandleAsync(connection, new Optimisarr.Core.Activity.ArrWebhookEvent.Test(), CancellationToken.None);

        Assert.Equal(ArrWebhookOutcome.Ignored, result.Outcome);
    }

    [Fact]
    public async Task A_chunked_body_over_the_limit_is_refused_without_a_content_length()
    {
        await using var hook = await CreateWebhookAsync();
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, hook.Path)
        {
            Content = new StreamContent(new UnseekableStream(new MemoryStream(new byte[2 * 1024 * 1024]))),
        };
        request.Headers.TransferEncodingChunked = true;
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"optimisarr:{hook.Password}")));

        Assert.Null(request.Content.Headers.ContentLength);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_connection_and_a_wrong_password_get_the_same_answer()
    {
        await using var hook = await CreateWebhookAsync();

        using var wrong = await DeliverAsync(hook, """{ "eventType": "Test" }""", "wrong-password");
        using var unknown = await DeliverAsync(hook with { Path = "/api/arr-webhooks/987654", Api = api }, """{ "eventType": "Test" }""");

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(wrong.StatusCode, unknown.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.Equal(wrong.Headers.WwwAuthenticate.ToString(), unknown.Headers.WwwAuthenticate.ToString());
        Assert.Equal(wrong.Content.Headers.ContentType, unknown.Content.Headers.ContentType);
    }

    private sealed class UnseekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("optimisarr-webhook-outside-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
