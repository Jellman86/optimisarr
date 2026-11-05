using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class CrossFilesystemDefaultMigrationTests : IDisposable
{
    private const string Migration = "AllowVerifiedCrossFilesystemReplacement";
    private readonly string _root = Directory.CreateTempSubdirectory("optimisarr-xfs-migration-").FullName;

    private OptimisarrDbContext Context() => new(new DbContextOptionsBuilder<OptimisarrDbContext>()
        .UseSqlite($"Data Source={Path.Combine(_root, "settings.db")};Pooling=False").Options);

    [Fact]
    public async Task A_saved_refusal_that_never_took_effect_becomes_allowed_and_other_settings_stay()
    {
        await using (var db = Context())
        {
            var migrations = db.Database.GetMigrations().ToList();
            var previous = migrations[migrations.FindIndex(name => name.EndsWith(Migration, StringComparison.Ordinal)) - 1];
            await db.GetService<IMigrator>().MigrateAsync(previous);
            db.AppSettings.AddRange(
                new AppSetting { Key = SettingKeys.ReplacementAllowCrossFilesystem, Value = "False" },
                new AppSetting { Key = SettingKeys.DryRunMode, Value = "False" });
            await db.SaveChangesAsync();
        }

        await using (var db = Context())
        {
            await db.Database.MigrateAsync();
            Assert.Equal("True", await Value(db, SettingKeys.ReplacementAllowCrossFilesystem));
            Assert.Equal("False", await Value(db, SettingKeys.DryRunMode));
        }

        await using (var db = Context())
        {
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal("True", await Value(db, SettingKeys.ReplacementAllowCrossFilesystem));
        }
    }

    private static Task<string> Value(OptimisarrDbContext db, string key) =>
        db.AppSettings.AsNoTracking().Where(setting => setting.Key == key).Select(setting => setting.Value!).SingleAsync();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
