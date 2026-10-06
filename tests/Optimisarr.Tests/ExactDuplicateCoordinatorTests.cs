using Optimisarr.Api.Library;
namespace Optimisarr.Tests;

public sealed class ExactDuplicateCoordinatorTests
{
    [Fact]
    public async Task Only_one_scan_runs_and_cancel_stops_its_owned_scan()
    {
        using var service = new ExactDuplicateCoordinator(new ExactDuplicateScanner(TimeSpan.FromSeconds(1)));
        await service.StartAsync(default);
        var root = Path.Combine(Path.GetTempPath(), "optimisarr-duplicate-coordinator-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var a = Path.Combine(root, "a"); var b = Path.Combine(root, "b");
            await File.WriteAllBytesAsync(a, new byte[300_000]); await File.WriteAllBytesAsync(b, new byte[300_000]);
            ExactDuplicateInput[] input = [new(1, a, "a", 300_000, File.GetLastWriteTimeUtc(a)), new(2, b, "b", 300_000, File.GetLastWriteTimeUtc(b))];
            Assert.True(service.TryStart(1, root, input));
            Assert.False(service.TryStart(2, root, input));
            Assert.False(service.Cancel(2)); Assert.True(service.Cancel(1));
            for (var i = 0; i < 100 && service.Read(1).Status is "Queued" or "Running"; i++) await Task.Delay(20);
            Assert.Equal("Cancelled", service.Read(1).Status);
            Assert.Null(service.Read(1).Result);
            Assert.Equal("NotStarted", service.Read(2).Status);
        }
        finally { await service.StopAsync(default); Directory.Delete(root, true); }
    }
}
