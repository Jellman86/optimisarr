using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Data;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

public sealed partial class WorkerResultUploadTests
{
    [PosixFact]
    public async Task Assignment_refuses_a_linked_source_before_preparing_work()
    {
        await EnableRemoteWorkers();
        using var worker = await PairWorker("Assignment boundary");
        await QueueAJob();
        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var media = await db.MediaFiles.SingleAsync(row => row.LibraryId == _createdLibraries.Last());
        var target = media.Path + ".original";
        File.Move(media.Path, target);
        File.CreateSymbolicLink(media.Path, target);
        using var response = await worker.PostAsJsonAsync("/api/workers/claim", new { });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(SourceBytes, await File.ReadAllBytesAsync(target));
        var job = await db.Jobs.SingleAsync(row => row.MediaFileId == media.Id);
        var capabilities = new WorkerCapabilities("Linux", "x64", ["libx265"], [], [], VmafCapability.None,
            FreeScratchBytes: 10_000_000_000, MaxConcurrency: 1);
        var plan = await _api.Services.GetRequiredService<QueueDispatcher>()
            .PrepareRemoteWorkAsync(job.Id, capabilities, default);
        Assert.Null(plan.Assignment);
        Assert.Contains("link", plan.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [PosixFact]
    public async Task Lease_source_rechecks_linked_parents_even_after_the_hash_was_cached()
    {
        await EnableRemoteWorkers();
        using var worker = await PairWorker("Source boundary");
        await QueueAJob();
        var assignment = await (await worker.PostAsJsonAsync("/api/workers/claim", new { }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var leaseId = assignment.GetProperty("leaseId").GetString()!;
        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var lease = await db.JobLeases.Include(row => row.Job).ThenInclude(job => job!.MediaFile)
            .SingleAsync(row => row.Id == Guid.Parse(leaseId));
        var source = lease.Job!.MediaFile!.Path;
        var directory = Path.GetDirectoryName(source)!;
        var moved = directory + "-moved";
        Directory.Move(directory, moved);
        Directory.CreateSymbolicLink(directory, moved);
        try
        {
            using var refused = await worker.GetAsync($"/api/workers/leases/{leaseId}/source");
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
            await db.Entry(lease.Job).ReloadAsync();
            Assert.Null(lease.Job.SourceSha256);
            Directory.Delete(directory);
            Directory.Move(moved, directory);
            using var accepted = await worker.GetAsync($"/api/workers/leases/{leaseId}/source");
            accepted.EnsureSuccessStatusCode();
            Directory.Move(directory, moved);
            Directory.CreateSymbolicLink(directory, moved);
            using var cached = await worker.GetAsync($"/api/workers/leases/{leaseId}/source");
            Assert.Equal(HttpStatusCode.NotFound, cached.StatusCode);
            Assert.False(cached.Headers.Contains("X-Optimisarr-Source-Sha256"));
        }
        finally
        {
            if (Directory.Exists(moved))
            {
                Directory.Delete(directory);
                Directory.Move(moved, directory);
            }
        }
    }
}
