using Optimisarr.Core.Tools;

namespace Optimisarr.Tests;

public sealed class LinuxMemoryAvailabilityTests
{
    private const string Host = "MemAvailable: 1048576 kB\n";
    private const string V2 = "29 23 0:26 / /sys/fs/cgroup rw,nosuid,nodev - cgroup2 cgroup rw\n";
    private const string V1 = "30 23 0:27 / /sys/fs/cgroup/memory rw - cgroup cgroup rw,memory\n";

    [Fact]
    public void Unified_limits_include_every_visible_ancestor()
    {
        var files = new Dictionary<string, string>
        {
            ["/sys/fs/cgroup/user/job/memory.max"] = "max",
            ["/sys/fs/cgroup/user/job/memory.current"] = "10",
            ["/sys/fs/cgroup/user/memory.max"] = "600000000",
            ["/sys/fs/cgroup/user/memory.current"] = "550000000"
        };
        Assert.Equal(50000000, Read("0::/user/job\n", V2, files));
    }

    [Theory]
    [InlineData("4:memory:/worker\n")]
    [InlineData("0::/unified\n4:memory:/worker\n")]
    public void Legacy_memory_controller_is_used_even_on_a_hybrid_host(string membership)
    {
        var files = new Dictionary<string, string>
        {
            ["/sys/fs/cgroup/memory/worker/memory.limit_in_bytes"] = "268435456",
            ["/sys/fs/cgroup/memory/worker/memory.usage_in_bytes"] = "33554432",
            ["/sys/fs/cgroup/memory/memory.limit_in_bytes"] = "9223372036854771712",
            ["/sys/fs/cgroup/memory/memory.usage_in_bytes"] = "1"
        };
        Assert.Equal(234881024, Read(membership, V2 + V1, files));
    }

    [Fact]
    public void Mounted_subtree_maps_the_host_relative_membership_and_stops_at_the_visible_root()
    {
        const string mount = "29 23 0:26 /tenant /run/cgroup rw - cgroup2 cgroup rw\n";
        var files = new Dictionary<string, string>
        {
            ["/run/cgroup/job/memory.max"] = "900", ["/run/cgroup/job/memory.current"] = "100",
            ["/run/cgroup/memory.max"] = "700", ["/run/cgroup/memory.current"] = "200"
        };
        Assert.Equal(500, Read("0::/tenant/job\n", mount, files));
        Assert.Equal(500, Read("0::/\n", mount, files));
    }

    [Fact]
    public void Escaped_mount_names_are_decoded_before_reading_limits()
    {
        const string mount = "29 23 0:26 / /run/cgroup\\040space rw - cgroup2 cgroup rw\n";
        Assert.Equal(50, Read("0::/\n", mount, new()
        { ["/run/cgroup space/memory.max"] = "100", ["/run/cgroup space/memory.current"] = "50" }));
    }

    [Theory]
    [InlineData("0::/missing\n", V2)]
    [InlineData("0::/../../escape\n", V2)]
    [InlineData("4:memory:/worker\n", V2)]
    [InlineData("0::/worker\n0::/other\n", V2)]
    [InlineData("garbage", V2)]
    [InlineData("0::/\n", "bad mountinfo")]
    public void Unresolved_or_ambiguous_controller_never_falls_back_to_host_memory(string membership, string mounts) =>
        Assert.Null(Read(membership, mounts, new()));

    [Theory]
    [InlineData("max", null)]
    [InlineData("100", "bad")]
    [InlineData("bad", "1")]
    [InlineData("-100", "1")]
    [InlineData("9223372036854775808", "1")]
    public void Incomplete_or_malformed_limit_pairs_are_unknown(string limit, string? usage)
    {
        var files = new Dictionary<string, string> { ["/sys/fs/cgroup/memory.max"] = limit };
        if (usage is not null) files["/sys/fs/cgroup/memory.current"] = usage;
        Assert.Null(Read("0::/\n", V2, files));
    }

    [Fact]
    public void Memory_pressure_limit_is_honoured_without_adding_swap_allowance() =>
        Assert.Equal(25, Read("0::/\n", V2, new()
        {
            ["/sys/fs/cgroup/memory.max"] = "1000", ["/sys/fs/cgroup/memory.current"] = "75",
            ["/sys/fs/cgroup/memory.high"] = "100", ["/sys/fs/cgroup/memory.swap.max"] = "10000000"
        }));

    [Fact]
    public void Root_without_a_memory_controller_has_only_the_physical_bound() =>
        Assert.Equal(1073741824, LinuxMemoryAvailability.Read(Host, "0::/\n", V2,
            _ => null, path => path == "/sys/fs/cgroup"));

    [Fact]
    public void Legacy_parent_limits_apply_when_the_child_limit_is_effectively_unlimited() =>
        Assert.Equal(50, Read("4:memory:/worker\n", V1, new()
        {
            ["/sys/fs/cgroup/memory/worker/memory.limit_in_bytes"] = "9223372036854771712",
            ["/sys/fs/cgroup/memory/worker/memory.usage_in_bytes"] = "1",
            ["/sys/fs/cgroup/memory/memory.limit_in_bytes"] = "100",
            ["/sys/fs/cgroup/memory/memory.usage_in_bytes"] = "50"
        }));

    [Theory]
    [InlineData("MemAvailable: 1 kB\nMemAvailable: 2 kB\n")]
    [InlineData("MemAvailable: 9223372036854775807 kB\n")]
    [InlineData("MemAvailable: -1 kB\n")]
    [InlineData("MemAvailable: 100 bytes\n")]
    public void Invalid_physical_reading_never_becomes_capacity(string meminfo) =>
        Assert.Null(LinuxMemoryAvailability.Read(meminfo, "0::/\n", V2, _ => null, _ => true));

    [Theory]
    [InlineData("0::/other/job\n")]
    [InlineData("0::/tenant-matching-prefix/job\n")]
    public void A_mount_for_another_subtree_is_not_a_namespace_fallback(string membership) =>
        Assert.Null(Read(membership, "29 23 0:26 /tenant /run/cgroup rw - cgroup2 cgroup rw\n", new()
        { ["/run/cgroup/memory.max"] = "100", ["/run/cgroup/memory.current"] = "50" }));

    private static long? Read(string membership, string mounts, Dictionary<string, string> files) =>
        LinuxMemoryAvailability.Read(Host, membership, mounts,
            path => files.GetValueOrDefault(path),
            path => path == "/sys/fs/cgroup" || files.Keys.Any(file => file[..file.LastIndexOf('/')] == path));
}
