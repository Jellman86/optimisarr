using Optimisarr.Api.Library;
using Optimisarr.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace Optimisarr.Tests;

public sealed class ExactDuplicateCoordinatorTests
{
    [Fact]
    public async Task Only_one_scan_runs_and_cancel_stops_its_owned_scan()
    {
        var root = Path.Combine(Path.GetTempPath(), "optimisarr-duplicate-coordinator-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        await using var services = new ServiceCollection()
            .AddDbContext<OptimisarrDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(root, "index.db")};Pooling=False"))
            .BuildServiceProvider();
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            await db.Database.MigrateAsync();
            db.Libraries.AddRange(new Library { Id = 1, Name = "One", Path = root }, new Library { Id = 2, Name = "Two", Path = root + "2" });
            await db.SaveChangesAsync();
        }
        using var service = new ExactDuplicateCoordinator(new ExactDuplicateScanner(TimeSpan.FromSeconds(1)),
            new ExactDuplicateStore(services.GetRequiredService<IServiceScopeFactory>()));
        await service.StartAsync(default);
        try
        {
            var a = Path.Combine(root, "a"); var b = Path.Combine(root, "b");
            await File.WriteAllBytesAsync(a, new byte[300_000]); await File.WriteAllBytesAsync(b, new byte[300_000]);
            var unique = Path.Combine(root, "unique"); await File.WriteAllBytesAsync(unique, new byte[7]);
            ExactDuplicateInput[] input = [new(1, a, "a", 300_000, File.GetLastWriteTimeUtc(a)), new(2, b, "b", 300_000, File.GetLastWriteTimeUtc(b)),
                new(3, unique, "unique", 7, File.GetLastWriteTimeUtc(unique))];
            Assert.True(await service.TryStartAsync(1, root, input, false, default));
            // The first status a browser sees must already count only same-size candidates, not every indexed file.
            var queued = (await service.ReadAsync(1, 0, default)).Progress;
            Assert.Equal((2, 600_000L), (queued.Total, queued.TotalBytes));
            Assert.False(await service.TryStartAsync(2, root, input, false, default));
            Assert.False(service.Cancel(2)); Assert.True(service.Cancel(1));
            for (var i = 0; i < 100 && (await service.ReadAsync(1, 0, default)).Status is "Queued" or "Running"; i++) await Task.Delay(20);
            Assert.Equal("Cancelled", (await service.ReadAsync(1, 0, default)).Status);
            Assert.Null((await service.ReadAsync(1, 0, default)).Result);
            Assert.Equal("NotStarted", (await service.ReadAsync(2, 0, default)).Status);
        }
        finally { await service.StopAsync(default); Directory.Delete(root, true); }
    }
}
