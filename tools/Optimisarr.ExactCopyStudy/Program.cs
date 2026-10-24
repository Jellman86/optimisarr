using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Api.Library;
using Optimisarr.Data;

// A disposable fixture study. It accepts no media paths and never opens a user library.
var root = Path.Combine(Path.GetTempPath(), "optimisarr-exact-copy-study-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await using var services = new ServiceCollection()
        .AddDbContext<OptimisarrDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(root, "study.db")};Pooling=False"))
        .AddSingleton<ExactDuplicateStore>().BuildServiceProvider();
    var inputs = new List<ExactDuplicateInput>();
    var catalogTimer = Stopwatch.StartNew();
    await using (var scope = services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await db.Database.MigrateAsync();
        db.Libraries.Add(new Library { Id = 1, Name = "Disposable study", Path = root });
        await db.SaveChangesAsync();
        for (var batch = 0; batch < 100; batch++)
        {
            db.MediaFiles.AddRange(Enumerable.Range(batch * 1_000 + 1, 1_000).Select(i => new MediaFile
            {
                LibraryId = 1, Path = Path.Combine(root, $"unopened-{i:D6}.mkv"), RelativePath = $"unopened-{i:D6}.mkv",
                SizeBytes = i, ModifiedAt = DateTimeOffset.UnixEpoch
            }));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        for (var i = 0; i < 16; i++)
        {
            var path = Path.Combine(root, $"fixture-{i:D2}.bin");
            var bytes = new byte[1_048_576]; Array.Fill(bytes, (byte)(i / 2 + 1));
            await File.WriteAllBytesAsync(path, bytes);
            var file = new MediaFile { LibraryId = 1, Path = path, RelativePath = Path.GetFileName(path),
                SizeBytes = bytes.Length, ModifiedAt = File.GetLastWriteTimeUtc(path) };
            db.MediaFiles.Add(file); await db.SaveChangesAsync();
            inputs.Add(new(file.Id, file.Path, file.RelativePath, file.SizeBytes, file.ModifiedAt));
        }
    }
    catalogTimer.Stop();
    var selectionTimer = Stopwatch.StartNew();
    await using (var scope = services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        inputs = await ExactDuplicateInputs.ReadAsync(db, 1, default);
        if (inputs.Count != 16) throw new InvalidOperationException("Unexpected inventory candidate count.");
    }
    selectionTimer.Stop();
    var baselineDatabaseBytes = Directory.EnumerateFiles(root, "study.db*").Sum(p => new FileInfo(p).Length);
    var store = services.GetRequiredService<ExactDuplicateStore>();
    using var scan = new ExactDuplicateCoordinator(new ExactDuplicateScanner(), store);
    await scan.StartAsync(default);
    try
    {
        var measurements = new List<object>();
        async Task<ExactDuplicateResult> Measure(string phase)
        {
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var timer = Stopwatch.StartNew();
            if (!await scan.TryStartAsync(1, root, inputs, false, default)) throw new InvalidOperationException("Study scan was busy.");
            ExactDuplicateStatus status;
            do
            {
                await Task.Delay(20);
                status = await scan.ReadAsync(1, 0, default);
                if (timer.Elapsed > TimeSpan.FromMinutes(2)) throw new TimeoutException("Study scan timed out.");
            } while (status.Status is "Queued" or "Running");
            if (status.Status != "Completed" || status.Result is null) throw new InvalidOperationException("Study scan failed.");
            timer.Stop(); process.Refresh();
            measurements.Add(new { phase, elapsedMs = timer.Elapsed.TotalMilliseconds,
                cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds, status.Result.BytesRead, status.Result.Reused,
                status.Result.TotalGroups, managedMemoryBytes = GC.GetTotalMemory(false), residentBytes = process.WorkingSet64,
                databaseBytes = Directory.EnumerateFiles(root, "study.db*").Sum(p => new FileInfo(p).Length) });
            return status.Result;
        }
        var first = await Measure("first-content-read");
        if (first.BytesRead != 16 * 1_048_576 || first.TotalGroups != 8) throw new InvalidOperationException("Cold read mismatch.");
        var unchanged = await Measure("unchanged-rescan");
        if (unchanged.BytesRead != 0 || unchanged.Reused != 16) throw new InvalidOperationException("Cache reuse mismatch.");
        var changed = inputs[0];
        await File.WriteAllBytesAsync(changed.Path, Enumerable.Repeat((byte)99, 1_048_576).ToArray());
        File.SetLastWriteTimeUtc(changed.Path, changed.ModifiedAt.AddMinutes(1).UtcDateTime);
        inputs[0] = changed with { ModifiedAt = File.GetLastWriteTimeUtc(changed.Path) };
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var file = await db.MediaFiles.SingleAsync(f => f.Id == changed.Id);
            file.ModifiedAt = inputs[0].ModifiedAt; await db.SaveChangesAsync();
        }
        var incremental = await Measure("one-file-change");
        if (incremental.BytesRead != 1_048_576 || incremental.Reused != 15 || incremental.TotalGroups != 7)
            throw new InvalidOperationException("Incremental read mismatch.");
        Console.WriteLine(JsonSerializer.Serialize(new { os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(), runtime = Environment.Version.ToString(),
            inventoryEntries = 100_016, candidateFiles = 16, baselineDatabaseBytes, catalogBuildMs = catalogTimer.Elapsed.TotalMilliseconds,
            candidateSelectionMs = selectionTimer.Elapsed.TotalMilliseconds, measurements }, new JsonSerializerOptions { WriteIndented = true }));
    }
    finally { await scan.StopAsync(default); }
}
finally { Directory.Delete(root, true); }
