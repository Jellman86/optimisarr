using Optimisarr.Api.Deduplication;
using System.Runtime.InteropServices;

namespace Optimisarr.Tests;

public sealed class FileOperationClaimsTests
{
    [Fact]
    public void A_multi_file_claim_is_atomic_and_a_losing_claim_leaks_nothing()
    {
        var claims = new FileOperationClaims();
        using var first = claims.TryAcquire(["media:1", "physical:1"]);
        Assert.NotNull(first);
        Assert.Null(claims.TryAcquire(["media:2", "physical:1"]));
        using var independent = claims.TryAcquire(["media:2"]);
        Assert.NotNull(independent);
        first.Dispose();
        using var retry = claims.TryAcquire(["physical:1"]);
        Assert.NotNull(retry);
    }

    [Fact]
    public void Duplicate_keys_and_disposing_twice_do_not_release_another_owners_claim()
    {
        var claims = new FileOperationClaims();
        var first = claims.TryAcquire(["a", "a", "b"])!;
        first.Dispose();
        using var second = claims.TryAcquire(["b", "a"]);
        first.Dispose();
        Assert.Null(claims.TryAcquire(["a"]));
    }

    [Fact]
    public async Task Reversed_multi_file_claims_have_one_winner_without_deadlock()
    {
        var claims = new FileOperationClaims();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var winners = 0;
        var leases = new System.Collections.Concurrent.ConcurrentBag<IDisposable>();
        await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(async () =>
        {
            await start.Task;
            var lease = claims.TryAcquire(i % 2 == 0 ? ["a", "b"] : ["b", "a"]);
            if (lease is not null) { Interlocked.Increment(ref winners); leases.Add(lease); }
        })).Prepend(Task.Run(() => start.SetResult())));
        Assert.Equal(1, winners);
        foreach (var lease in leases) lease.Dispose();
        using var next = claims.TryAcquire(["a", "b"]);
        Assert.NotNull(next);
    }

    [Fact]
    public void Empty_or_invalid_claims_are_rejected()
    {
        var claims = new FileOperationClaims();
        Assert.Throws<ArgumentException>(() => claims.TryAcquire([]));
        Assert.Throws<ArgumentException>(() => claims.TryAcquire([""]));
    }
    [Fact]
    public async Task Real_hard_links_with_different_inventory_ids_share_the_physical_claim()
    {
        var root = Path.Combine(Path.GetTempPath(),"optimisarr-claims-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var original = Path.Combine(root,"original");
            var alias = Path.Combine(root,"alias");
            await File.WriteAllTextAsync(original,"original bytes");
            Assert.True(OperatingSystem.IsWindows() ? CreateHardLink(alias,original,IntPtr.Zero) : Link(original,alias) == 0);
            var first = FileOperationClaims.Keys(1,original);
            var second = FileOperationClaims.Keys(2,alias);
            Assert.Single(first.Intersect(second),k => k.StartsWith("identity:",StringComparison.Ordinal));
            var claims = new FileOperationClaims();
            using var held = claims.TryAcquire(first);
            Assert.Null(claims.TryAcquire(second));
            Assert.Equal("original bytes",await File.ReadAllTextAsync(alias));
        }
        finally { Directory.Delete(root,true); }
    }

    [Fact]
    public void Lexical_aliases_share_a_path_claim_even_when_the_file_is_missing()
    {
        var root=Path.GetTempPath();
        var first=FileOperationClaims.Keys(1,Path.Combine(root,"a","..","missing"));
        var second=FileOperationClaims.Keys(2,Path.Combine(root,"missing"));
        Assert.Single(first.Intersect(second),k => k.StartsWith("path:",StringComparison.Ordinal));
    }

    [DllImport("libc",EntryPoint="link",SetLastError=true)]
    private static extern int Link(string source,string target);
    [DllImport("kernel32.dll",EntryPoint="CreateHardLinkW",CharSet=CharSet.Unicode,SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string target,string source,IntPtr reserved);

}
