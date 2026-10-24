using System.Data;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Diagnostics;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class DiagnosticWriteConcurrencyTests
{
    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 0)]
    [InlineData(true, true, 1500)]
    public async Task An_outer_transaction_can_commit_while_another_job_save_waits_for_the_database(bool capturing, bool finalSlot, int outerWriteDelayMilliseconds)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var options = new DbContextOptionsBuilder<OptimisarrDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "test.db")};Pooling=False").Options;
        try
        {
            await using var first = new OptimisarrDbContext(options);
            await first.Database.MigrateAsync();
            var media = new MediaFile { Path = "/synthetic/concurrency.mkv", RelativePath = "concurrency.mkv" };
            first.MediaFiles.Add(media); await first.SaveChangesAsync();
            var a = new Job { MediaFileId = media.Id }; var b = new Job { MediaFileId = media.Id };
            first.AddRange(a, b); await first.SaveChangesAsync();
            if (capturing) await new DiagnosticCaptureStore(first).StartAsync(1, null, false, DateTimeOffset.UtcNow, default);
            if (finalSlot)
            {
                var session = await first.DiagnosticCaptureSessions.SingleAsync();
                session.EventsStored = 9999; await first.SaveChangesAsync();
            }
            await using var second = new OptimisarrDbContext(options);
            var secondJob = await second.Jobs.SingleAsync(j => j.Id == b.Id);
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            second.Database.GetDbConnection().StateChange += (_, e) => { if (e.CurrentState == ConnectionState.Open) opened.TrySetResult(); };
            await using var transaction = await first.Database.BeginTransactionAsync();
            secondJob.Status = JobStatus.Failed;
            // SQLite waits synchronously for the writer; a dedicated contender avoids starving
            // the continuation that must release the outer transaction on a busy test runner.
            var pending = Task.Factory.StartNew(() => second.SaveChangesAsync(), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(outerWriteDelayMilliseconds);
            a.Status = JobStatus.Failed;
            await first.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await transaction.CommitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, await first.Jobs.CountAsync(j => j.Status == JobStatus.Failed));
            if (capturing)
            {
                Assert.Equal(finalSlot ? 1 : 2, await first.DiagnosticEvents.CountAsync());
                var session = await first.DiagnosticCaptureSessions.AsNoTracking().SingleAsync();
                Assert.Equal(finalSlot ? 10000 : 2, session.EventsStored);
                Assert.Equal(finalSlot, session.EventLimitReached);
                Assert.True(session.BytesStored <= session.MaximumBytes);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
