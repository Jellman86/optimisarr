using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Deduplication;
using Optimisarr.Api.Endpoints;
using Optimisarr.Api.Library;
using Optimisarr.Api.Workers;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

// SSD/cache-backed synthetic I/O only. No media arguments, production DB or host configuration.
var root = Path.Combine(Path.GetTempPath(), "optimisarr-upload-study-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite($"Data Source={Path.Combine(root, "study.db")};Pooling=False").Options;
const string credential = "disposable-upload-study-credential";
var measurements = new List<object>();
try
{
    await using (var setup = new OptimisarrDbContext(options))
    {
        await setup.Database.MigrateAsync();
        setup.Libraries.Add(new() { Id = 1, Name = "Disposable study", Path = root });
        setup.MediaFiles.Add(new() { Id = 1, LibraryId = 1, Path = Path.Combine(root, "source.bin"), RelativePath = "source.bin" });
        setup.Workers.Add(new() { Id = 1, Name = "Synthetic worker", CredentialFingerprint = WorkerCredential.Fingerprint(credential) });
        setup.AppSettings.Add(new() { Key = SettingKeys.RemoteWorkersEnabled, Value = "True" });
        await setup.SaveChangesAsync();
    }
    foreach (var mode in new[] { "bounded-128KiB", "bounded-1MiB", "bounded-1MiB-yield1ms", "bounded-1MiB-yield5ms", "durable-owner-ideal-prototype" })
    for (var iteration = 0; iteration < 3; iteration++)
    {
        await using var db = new OptimisarrDbContext(options);
        var job = new Job { MediaFileId = 1, LibraryId = 1, Status = JobStatus.Leased, SourceSha256 = new('a', 64) };
        db.Jobs.Add(job); await db.SaveChangesAsync();
        var lease = new JobLease { Id = Guid.NewGuid(), JobId = job.Id, WorkerId = 1, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), OutputExtension = ".mkv" };
        db.JobLeases.Add(lease); await db.SaveChangesAsync();
        var staging = Path.Combine(root, $"remote-{job.Id}.{lease.Id:N}.partial");
        var snapshot = new WorkerResultEndpoints.UploadSnapshot(lease.Id, job.Id, 1, job.ExecutionAttempt,
            lease.ExecutionAttempt, lease.WorkerId, job.SourceSha256, Path.Combine(root, "source.bin"), null, ".mkv", staging, staging + ".mkv");
        var context = new DefaultHttpContext(); context.Request.Headers.Authorization = "Bearer " + credential;
        var settings = new SettingsStore(db, new RemoteWorkersFeature(true));
        var claims = new FileOperationClaims();
        var start = await WorkerResultEndpoints.StartUploadAsync(db, claims, snapshot, context.Request, settings, false, 0, default);
        if (start.Refusal is not null) throw new InvalidOperationException("Study setup refused.");
        var buffer = new byte[mode == "bounded-128KiB" ? 128 * 1024 : 1024 * 1024];
        Array.Fill(buffer, (byte)73);
        const long totalBytes = 256L * 1024 * 1024;
        var writerLatency = new List<double>();
        using var stop = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherWriter = Task.Run(async () =>
        {
            await using var other = new OptimisarrDbContext(options);
            ready.SetResult();
            while (!stop.IsCancellationRequested)
            {
                var elapsed = Stopwatch.StartNew();
                await using (var write = await other.Database.BeginTransactionAsync())
                {
                    await other.Database.ExecuteSqlRawAsync("UPDATE Workers SET LastSeenAt = LastSeenAt WHERE Id = 1");
                    await write.CommitAsync();
                }
                writerLatency.Add(elapsed.Elapsed.TotalMilliseconds);
                await Task.Delay(2);
            }
        });
        await ready.Task;
        var timer = Stopwatch.StartNew();
        if (mode.StartsWith("bounded", StringComparison.Ordinal))
        {
            for (long offset = 0; offset < totalBytes; offset += buffer.Length)
            {
                var refusal = await WorkerResultEndpoints.AppendUploadBufferAsync(db, claims, snapshot, start.Upload!, context.Request,
                    settings, buffer, offset, default);
                if (refusal is not null) throw new InvalidOperationException("Study append refused.");
                if (mode.EndsWith("yield1ms", StringComparison.Ordinal)) await Task.Delay(1);
                else if (mode.EndsWith("yield5ms", StringComparison.Ordinal)) await Task.Delay(5);
                else await Task.Yield();
            }
        }
        else
        {
            // Optimistic upper bound: assumes a durable owner already excludes every other file
            // path. It does NOT implement the required crash reconciliation or reservation adapter.
            await using (var file = new FileStream(staging, FileMode.Open, FileAccess.Write, FileShare.None, 1, true))
            {
                for (long offset = 0; offset < totalBytes; offset += buffer.Length)
                {
                    await file.WriteAsync(buffer); await file.FlushAsync(); await Task.Yield();
                }
            }
        }
        timer.Stop(); stop.Cancel(); await otherWriter;
        writerLatency.Sort();
        measurements.Add(new { mode, iteration, bytes = totalBytes, seconds = timer.Elapsed.TotalSeconds,
            mebibytesPerSecond = totalBytes / 1048576d / timer.Elapsed.TotalSeconds, competingWriterSamples = writerLatency.Count,
            competingWriterP95Ms = writerLatency.Count == 0 ? 0 : writerLatency[(int)((writerLatency.Count - 1) * .95)],
            competingWriterMaxMs = writerLatency.Count == 0 ? 0 : writerLatency[^1] });
        File.Delete(staging);
    }
    Console.WriteLine(JsonSerializer.Serialize(new { os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        runtime = Environment.Version.ToString(), limitation = "Synthetic SSD/page-cache fixture; no fsync, network, SMR/USB or production concurrency qualification. Durable owner is an optimistic prototype, not a safe shipping implementation.", measurements }, new JsonSerializerOptions { WriteIndented = true }));
}
finally { Directory.Delete(root, true); }
