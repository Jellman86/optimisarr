using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class MediaKindBackfillTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;

    public MediaKindBackfillTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Resets_only_probed_unknown_files_and_records_the_flag()
    {
        await SeedAsync(
            ("/data/film.mp4", MediaFileStatus.Probed, MediaKind.Unknown),
            ("/data/show.mkv", MediaFileStatus.Probed, MediaKind.Video),
            ("/data/new.mkv", MediaFileStatus.Discovered, MediaKind.Unknown));

        await using var db = new OptimisarrDbContext(_options);
        var reset = await MediaKindBackfill.ResetUnknownProbedFilesAsync(db, CancellationToken.None);

        Assert.Equal(1, reset);
        // The stale probed-Unknown file is queued for re-probe; the others are untouched.
        Assert.Equal(MediaFileStatus.Discovered, await StatusOf(db, "/data/film.mp4"));
        Assert.Equal(MediaFileStatus.Probed, await StatusOf(db, "/data/show.mkv"));
        Assert.Equal(MediaFileStatus.Discovered, await StatusOf(db, "/data/new.mkv"));
        Assert.True(await db.AppSettings.AnyAsync(s => s.Key == SettingKeys.MediaKindBackfillDone));
    }

    [Fact]
    public async Task Is_a_no_op_on_a_second_run()
    {
        await SeedAsync(("/data/film.mp4", MediaFileStatus.Probed, MediaKind.Unknown));

        await using (var first = new OptimisarrDbContext(_options))
        {
            Assert.Equal(1, await MediaKindBackfill.ResetUnknownProbedFilesAsync(first, CancellationToken.None));
        }

        // A file that becomes Unknown again later must not be reset a second time.
        await using (var probedAgain = new OptimisarrDbContext(_options))
        {
            var file = await probedAgain.MediaFiles.FirstAsync();
            file.Status = MediaFileStatus.Probed;
            await probedAgain.SaveChangesAsync();
        }

        await using var db = new OptimisarrDbContext(_options);
        Assert.Equal(0, await MediaKindBackfill.ResetUnknownProbedFilesAsync(db, CancellationToken.None));
        Assert.Equal(MediaFileStatus.Probed, await StatusOf(db, "/data/film.mp4"));
    }

    private async Task SeedAsync(params (string Path, MediaFileStatus Status, MediaKind Kind)[] files)
    {
        await using var db = new OptimisarrDbContext(_options);
        foreach (var (path, status, kind) in files)
        {
            db.MediaFiles.Add(new MediaFile
            {
                Path = path,
                RelativePath = Path.GetFileName(path),
                SizeBytes = 1,
                Status = status,
                MediaKind = kind
            });
        }
        await db.SaveChangesAsync();
    }

    private static async Task<MediaFileStatus> StatusOf(OptimisarrDbContext db, string path) =>
        (await db.MediaFiles.AsNoTracking().FirstAsync(f => f.Path == path)).Status;

    public void Dispose() => _connection.Dispose();
}
