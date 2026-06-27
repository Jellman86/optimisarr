using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Diagnostics;
using Optimisarr.Api.Library;
using Optimisarr.Api.Stats;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class DiagnosticsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;

    public DiagnosticsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public void Redaction_keeps_names_types_and_flags_but_omits_secrets()
    {
        var watcher = new ActivityWatcher
        {
            Name = "Living room Plex", Type = ActivityWatcherType.Plex, BaseUrl = "http://plex.local:32400",
            ApiToken = "PLEX-SECRET-TOKEN", Enabled = true, RefreshOnReplace = true
        };
        var target = new NotificationTarget
        {
            Name = "Alerts", Type = NotificationType.Discord,
            Url = "https://discord.com/api/webhooks/123/WEBHOOK-SECRET", Token = "TARGET-SECRET",
            Enabled = true, NotifyOnReplacement = false, NotifyOnFailure = true
        };
        var arr = new ArrConnection
        {
            Name = "Sonarr", Type = ArrConnectionType.Sonarr, BaseUrl = "http://sonarr.local:8989",
            ApiKey = "SONARR-API-KEY", Enabled = true
        };

        var summary = DiagnosticsRedaction.Summarise([watcher], [target], [arr]);

        // Safe fields survive.
        Assert.Equal("Living room Plex", summary.ActivityWatchers[0].Name);
        Assert.True(summary.ActivityWatchers[0].RefreshOnReplace);
        Assert.Equal("Discord", summary.NotificationTargets[0].Type);
        Assert.True(summary.NotificationTargets[0].NotifyOnFailure);
        Assert.Equal("Sonarr", summary.ArrConnections[0].Name);

        // No secret (token, api key, webhook URL, or base URL) appears anywhere in the serialized form.
        var json = JsonSerializer.Serialize(summary);
        foreach (var secret in new[]
                 {
                     "PLEX-SECRET-TOKEN", "WEBHOOK-SECRET", "TARGET-SECRET", "SONARR-API-KEY",
                     "discord.com", "plex.local", "sonarr.local"
                 })
        {
            Assert.DoesNotContain(secret, json);
        }
    }

    [Fact]
    public async Task BuildAsync_assembles_a_secret_free_bundle_with_libraries_and_failures()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library { Name = "Films", Path = "/data/films" };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();

            var fileA = new MediaFile { LibraryId = library.Id, Path = "/data/films/a.mkv", RelativePath = "a.mkv", SizeBytes = 1, Status = MediaFileStatus.Probed };
            db.MediaFiles.Add(fileA);
            db.MediaFiles.Add(new MediaFile { LibraryId = library.Id, Path = "/data/films/b.mkv", RelativePath = "b.mkv", SizeBytes = 1, Status = MediaFileStatus.Probed });
            await db.SaveChangesAsync();

            db.Jobs.Add(new Job { MediaFileId = fileA.Id, LibraryId = library.Id, Status = JobStatus.Failed, ErrorMessage = "Verification failed: Size saving", FinishedAt = DateTimeOffset.UtcNow });
            db.ArrConnections.Add(new ArrConnection { Name = "Sonarr", Type = ArrConnectionType.Sonarr, BaseUrl = "http://sonarr.local", ApiKey = "TOP-SECRET-KEY", Enabled = true });
            await db.SaveChangesAsync();
        }

        await using var readDb = new OptimisarrDbContext(_options);
        var environment = new DiagnosticsEnvironment("TestOS", ".NET", "/config", true);
        var bundle = await DiagnosticsQueries.BuildAsync(
            readDb, new SettingsStore(readDb), new LifetimeStatsStore(readDb), environment, "1.2.3.4", CancellationToken.None);

        Assert.Equal("1.2.3.4", bundle.Version);
        var film = Assert.Single(bundle.Libraries);
        Assert.Equal("Films", film.Name);
        Assert.Equal(2, film.FileCount);
        Assert.Equal("Sonarr", Assert.Single(bundle.Integrations.ArrConnections).Name);
        Assert.Equal("SizeSaving", Assert.Single(bundle.Failures).Category);

        // The whole bundle, serialized, must never carry the Arr API key.
        Assert.DoesNotContain("TOP-SECRET-KEY", JsonSerializer.Serialize(bundle));
    }

    public void Dispose() => _connection.Dispose();
}
