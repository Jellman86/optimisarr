using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Optimisarr.Api.Endpoints;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class BatchOperationTests
{
    [Fact]
    public async Task An_item_IO_failure_preserves_earlier_results_and_continues_the_batch()
    {
        await using var db = new OptimisarrDbContext(new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite("Data Source=:memory:").Options);
        var called = new List<int>();
        var result = await BatchEndpoints.RunAsync([1, 2, 3], db, id => {
            called.Add(id);
            if (id == 2) throw new IOException("Private filesystem detail");
            return Task.FromResult<BatchFailure?>(null);
        }, NullLogger.Instance, CancellationToken.None);
        Assert.Equal([1, 2, 3], called);
        Assert.Equal([1, 3], result.Succeeded);
        var failed = Assert.Single(result.Failures);
        Assert.Equal(2, failed.Id);
        Assert.Equal("batch.itemFailed", failed.Code);
        Assert.DoesNotContain("Private", failed.Message);
    }

    [Fact]
    public async Task Request_cancellation_is_not_misreported_as_an_individual_failure()
    {
        await using var db = new OptimisarrDbContext(new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite("Data Source=:memory:").Options);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BatchEndpoints.RunAsync([1], db,
            _ => Task.FromResult<BatchFailure?>(null), NullLogger.Instance, cancellation.Token));
    }
}
