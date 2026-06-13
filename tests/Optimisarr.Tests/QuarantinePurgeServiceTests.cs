using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Optimisarr.Api.Library;
using Optimisarr.Api.Replacement;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class QuarantinePurgeServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;
    private readonly string _trashDir;

    public QuarantinePurgeServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();

        _trashDir = Path.Combine(Path.GetTempPath(), "optimisarr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_trashDir);
    }

    [Fact]
    public async Task Purge_does_nothing_when_retention_is_indefinite()
    {
        await SetRetentionDaysAsync(0);
        var (id, quarantinePath) = await SeedQuarantinedAsync(replacedAt: DateTimeOffset.UtcNow.AddYears(-1));

        var purged = await PurgeAsync();

        Assert.Equal(0, purged);
        Assert.True(File.Exists(quarantinePath));
        Assert.Equal(ReplacementStatus.Replaced, await StatusOfAsync(id));
    }

    [Fact]
    public async Task Purge_deletes_originals_past_the_retention_window_and_marks_them_purged()
    {
        await SetRetentionDaysAsync(30);
        var (id, quarantinePath) = await SeedQuarantinedAsync(replacedAt: DateTimeOffset.UtcNow.AddDays(-31));
        var stampDir = Path.GetDirectoryName(quarantinePath)!;

        var purged = await PurgeAsync();

        Assert.Equal(1, purged);
        Assert.False(File.Exists(quarantinePath));   // original deleted
        Assert.False(Directory.Exists(stampDir));     // empty quarantine folder removed too

        await using var db = new OptimisarrDbContext(_options);
        var replacement = await db.Replacements.SingleAsync();
        Assert.Equal(ReplacementStatus.Purged, replacement.Status);
        Assert.NotNull(replacement.PurgedAt);
    }

    [Fact]
    public async Task Purge_keeps_originals_still_within_the_retention_window()
    {
        await SetRetentionDaysAsync(30);
        var (id, quarantinePath) = await SeedQuarantinedAsync(replacedAt: DateTimeOffset.UtcNow.AddDays(-5));

        var purged = await PurgeAsync();

        Assert.Equal(0, purged);
        Assert.True(File.Exists(quarantinePath));
        Assert.Equal(ReplacementStatus.Replaced, await StatusOfAsync(id));
    }

    [Fact]
    public async Task Purge_ignores_already_rolled_back_replacements()
    {
        await SetRetentionDaysAsync(30);
        var (id, _) = await SeedQuarantinedAsync(
            replacedAt: DateTimeOffset.UtcNow.AddYears(-1),
            status: ReplacementStatus.RolledBack);

        var purged = await PurgeAsync();

        Assert.Equal(0, purged);
        Assert.Equal(ReplacementStatus.RolledBack, await StatusOfAsync(id));
    }

    [Fact]
    public async Task Purge_still_marks_an_entry_purged_when_the_file_is_already_gone()
    {
        await SetRetentionDaysAsync(30);
        var (id, quarantinePath) = await SeedQuarantinedAsync(replacedAt: DateTimeOffset.UtcNow.AddDays(-40));
        File.Delete(quarantinePath);   // someone removed it manually

        var purged = await PurgeAsync();

        Assert.Equal(1, purged);
        Assert.Equal(ReplacementStatus.Purged, await StatusOfAsync(id));
    }

    [Fact]
    public async Task Purge_one_deletes_the_original_now_regardless_of_the_retention_window()
    {
        await SetRetentionDaysAsync(0); // indefinite retention — an on-demand approve still purges
        var (id, quarantinePath) = await SeedQuarantinedAsync(replacedAt: DateTimeOffset.UtcNow);
        var stampDir = Path.GetDirectoryName(quarantinePath)!;

        var result = await PurgeOneAsync(id);

        Assert.Equal(ReplacementResultKind.Success, result.Kind);
        Assert.False(File.Exists(quarantinePath));
        Assert.False(Directory.Exists(stampDir));
        Assert.Equal(ReplacementStatus.Purged, await StatusOfAsync(id));

        await using var db = new OptimisarrDbContext(_options);
        Assert.NotNull((await db.Replacements.SingleAsync(r => r.Id == id)).PurgedAt);
    }

    [Fact]
    public async Task Purge_one_refuses_a_replacement_that_is_not_in_quarantine()
    {
        var (id, quarantinePath) = await SeedQuarantinedAsync(
            replacedAt: DateTimeOffset.UtcNow, status: ReplacementStatus.RolledBack);

        var result = await PurgeOneAsync(id);

        Assert.Equal(ReplacementResultKind.Invalid, result.Kind);
        Assert.True(File.Exists(quarantinePath));                       // nothing deleted
        Assert.Equal(ReplacementStatus.RolledBack, await StatusOfAsync(id));
    }

    [Fact]
    public async Task Purge_one_reports_not_found_for_an_unknown_id()
    {
        var result = await PurgeOneAsync(9999);

        Assert.Equal(ReplacementResultKind.NotFound, result.Kind);
    }

    private async Task<ReplacementActionResult> PurgeOneAsync(int replacementId)
    {
        await using var db = new OptimisarrDbContext(_options);
        var service = new QuarantinePurgeService(
            db, new SettingsStore(db), NullLogger<QuarantinePurgeService>.Instance);
        return await service.PurgeOneAsync(replacementId, CancellationToken.None);
    }

    private async Task<int> PurgeAsync()
    {
        await using var db = new OptimisarrDbContext(_options);
        var service = new QuarantinePurgeService(
            db, new SettingsStore(db), NullLogger<QuarantinePurgeService>.Instance);
        return await service.PurgeExpiredAsync(CancellationToken.None);
    }

    private async Task<(int Id, string QuarantinePath)> SeedQuarantinedAsync(
        DateTimeOffset replacedAt,
        ReplacementStatus status = ReplacementStatus.Replaced)
    {
        var stampDir = Path.Combine(_trashDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stampDir);
        var quarantinePath = Path.Combine(stampDir, "Movie.avi");
        File.WriteAllText(quarantinePath, "ORIGINAL-DATA");

        await using var db = new OptimisarrDbContext(_options);
        var media = new MediaFile
        {
            Path = Path.Combine(_trashDir, $"placeholder-{Guid.NewGuid():N}.mkv"),
            RelativePath = "Movie.mkv",
            SizeBytes = 1,
            Status = MediaFileStatus.Probed
        };
        db.MediaFiles.Add(media);
        await db.SaveChangesAsync();

        var job = new Job { MediaFileId = media.Id, Status = JobStatus.Completed };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        var replacement = new Optimisarr.Data.Replacement
        {
            JobId = job.Id,
            MediaFileId = media.Id,
            OriginalPath = "/data/Movie.avi",
            QuarantinePath = quarantinePath,
            FinalPath = "/data/Movie.mkv",
            OriginalSizeBytes = 13,
            NewSizeBytes = 7,
            Status = status,
            ReplacedAt = replacedAt
        };
        db.Replacements.Add(replacement);
        await db.SaveChangesAsync();
        return (replacement.Id, quarantinePath);
    }

    private async Task SetRetentionDaysAsync(int days)
    {
        await using var db = new OptimisarrDbContext(_options);
        var settings = new SettingsStore(db);
        var current = await settings.GetQueueSettingsAsync(CancellationToken.None);
        await settings.SetQueueSettingsAsync(
            current with { ReplacementQuarantineRetentionDays = days }, CancellationToken.None);
    }

    private async Task<ReplacementStatus> StatusOfAsync(int replacementId)
    {
        await using var db = new OptimisarrDbContext(_options);
        var replacement = await db.Replacements.SingleAsync(r => r.Id == replacementId);
        return replacement.Status;
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_trashDir))
        {
            Directory.Delete(_trashDir, recursive: true);
        }
    }
}
