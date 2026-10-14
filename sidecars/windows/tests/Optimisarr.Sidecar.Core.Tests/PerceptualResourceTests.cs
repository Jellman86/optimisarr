using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class PerceptualResourceTests
{
    [Fact]
    public void Parallel_jobs_reserve_their_peak_workspace_and_memory_until_cleanup_finishes()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var pool = new PerceptualJobResourcePool();
        using var first = pool.TryAcquire(root, 600, 400, 1000, 1000);
        Assert.NotNull(first);
        Assert.Null(pool.TryAcquire(root, 500, 100, 1000, 1000));
        using var otherDisk = pool.TryAcquire(root + "-other", 900, 500, 1000, 1000);
        Assert.NotNull(otherDisk);
        Assert.Null(pool.TryAcquire(root + "-third", 1, 200, 1000, 1000));
        first.Dispose(); first.Dispose(); otherDisk.Dispose();
        using var resumed = pool.TryAcquire(root, 1000, 1000, 1000, 1000);
        Assert.NotNull(resumed);
    }

    [Theory]
    [InlineData(-1, 1, 100L, 100L)]
    [InlineData(1, -1, 100L, 100L)]
    [InlineData(1, 1, null, 100L)]
    [InlineData(1, 1, 100L, null)]
    public void Invalid_or_unavailable_resources_do_not_become_unlimited_capacity(long disk, long memory, long? freeDisk, long? freeMemory) =>
        Assert.Null(new PerceptualJobResourcePool().TryAcquire(Guid.NewGuid().ToString(), disk, memory, freeDisk, freeMemory));

    [Theory]
    [InlineData("MemAvailable: 1024 kB\n", "max", "0", 1048576L)]
    [InlineData("MemAvailable: 1024 kB\n", "600000", "200000", 400000L)]
    [InlineData("MemAvailable: 1024 kB\n", "100", "200", 0L)]
    [InlineData("MemAvailable: 1024 kB\n", "bad", "0", null)]
    [InlineData("MemAvailable: 1024 kB\n", "100", "bad", null)]
    [InlineData("MemTotal: 1024 kB\n", "max", "0", null)]
    [InlineData("MemAvailable: -2 kB\n", "max", "0", null)]
    public void Linux_memory_admission_uses_current_available_bytes_and_the_remaining_cgroup_limit(string meminfo, string limit, string used, long? expected) =>
        Assert.Equal(expected, Optimisarr.Core.Tools.LinuxMemoryAvailability.ApplyLimits(meminfo, [(limit, used)]));

    [Fact]
    public void Parent_cgroup_limits_are_applied_even_when_the_current_group_is_unlimited() =>
        Assert.Equal(100, Optimisarr.Core.Tools.LinuxMemoryAvailability.ApplyLimits("MemAvailable: 1024 kB\n", [("max", "0"), ("1000", "900")]));

    [Theory]
    [InlineData(0u, 0UL, 0UL, 0UL, 1000L)]
    [InlineData(0x200u, 0UL, 600UL, 450UL, 150L)]
    [InlineData(0x200u, 0UL, 600UL, 800UL, 0L)]
    [InlineData(0x100u, 200UL, 0UL, 0UL, 200L)]
    public void Windows_job_and_child_process_limits_cannot_be_hidden_by_host_free_memory(uint flags, ulong process, ulong job, ulong peak, long expected) =>
        Assert.Equal(expected, NativeMemoryAvailability.ApplyWindowsLimits(1000, flags, process, job, peak));

    [Fact]
    public void Supported_native_memory_providers_return_a_bounded_current_reading()
    {
        var available = NativeMemoryAvailability.Read();
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) Assert.True(available is >= 0);
        else Assert.Null(available);
    }

    [Fact]
    public void Working_space_links_are_resolved_before_identifying_the_volume_and_RAM_budget()
    {
        if (OperatingSystem.IsWindows()) return; // Windows link privilege is independent of this pure resource policy.
        var root = Path.Combine(Path.GetTempPath(), "native-volume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "target"));
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "alias"), Path.Combine(root, "target"));
            var direct = PerceptualJobRuntime.PhysicalRoot(Path.Combine(root, "target", "not-created"));
            var linked = PerceptualJobRuntime.PhysicalRoot(Path.Combine(root, "alias", "not-created"));
            Assert.Equal(direct, linked); Assert.DoesNotContain("alias", linked);
        }
        finally { Directory.Delete(root, true); }
    }
}
