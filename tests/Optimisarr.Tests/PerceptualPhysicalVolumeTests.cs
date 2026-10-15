using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

public sealed class PerceptualPhysicalVolumeTests
{
    private static PhysicalVolumeId Volume(string value = "device:one") => new(value);

    [Fact]
    public void Physical_identity_is_opaque_and_never_normalized_as_a_path()
    {
        var pool = PerceptualJobResourcePool.ForPhysicalVolumes();
        var first = Volume("device:a/../b");
        using var held = pool.TryAcquire(first, 600, 100, 1000, 1000);
        Assert.NotNull(held);
        Assert.Equal((400L, 900L), pool.Available(Volume("device:a/../b"), 1000, 1000));
        Assert.Equal((1000L, 900L), pool.Available(Volume("device:b"), 1000, 1000));
        Assert.Equal((1000L, 900L), pool.Available(Volume("device:a/../b/"), 1000, 1000));
    }

    [Fact]
    public void Physical_identity_comparison_preserves_case_on_every_platform()
    {
        var pool = PerceptualJobResourcePool.ForPhysicalVolumes();
        using var held = pool.TryAcquire(Volume("device:A"), 600, 100, 1000, 1000);
        Assert.Equal((400L, 900L), pool.Available(Volume("device:A"), 1000, 1000));
        Assert.Equal((1000L, 900L), pool.Available(Volume("device:a"), 1000, 1000));
    }

    [Fact]
    public void An_equal_reported_identity_shares_debt_and_idempotent_owner_restoration()
    {
        var pool = PerceptualJobResourcePool.ForPhysicalVolumes();
        var owner = Guid.NewGuid();
        Assert.True(pool.TryRestore(owner, Volume(), 600, 400));
        Assert.True(pool.TryRestore(owner, Volume(), 600, 400));
        Assert.False(pool.TryRestore(owner, Volume("device:other"), 600, 400));
        Assert.False(pool.TryRestore(owner, Volume(), 601, 400));
        Assert.False(pool.TryReserve(owner, Volume("device:other"), 1, 1, 1000, 1000));
        Assert.False(pool.TryRestore(owner, Path.GetFullPath("same-owner-alias"), 600, 400));
        Assert.False(pool.TryReserve(Guid.NewGuid(), Volume(), 401, 1, 1000, 1000));
        Assert.Equal((400L, 600L), pool.Available(Volume(), 1000, 1000));
        Assert.True(pool.Release(owner));
        Assert.False(pool.Release(owner));
        Assert.Equal((1000L, 1000L), pool.Available(Volume(), 1000, 1000));
    }

    [Fact]
    public void Null_or_uninitialized_physical_identifiers_cannot_bypass_any_pool_boundary()
    {
        var pool = PerceptualJobResourcePool.ForPhysicalVolumes();
        using var held = pool.TryAcquire(Volume(), 600, 400, 1000, 1000);
        var uninitialized = (PhysicalVolumeId)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PhysicalVolumeId));
        foreach (var invalid in new[] { null!, uninitialized }) {
            var owner = Guid.NewGuid();
            Assert.False(pool.TryRestore(owner, invalid, 1, 1));
            Assert.False(pool.TryReserve(owner, invalid, 1, 1, 1000, 1000));
            Assert.Null(pool.TryAcquire(invalid, 1, 1, 1000, 1000));
            Assert.Equal((0L, 0L), pool.Available(invalid, 1000, 1000));
            Assert.False(pool.Release(owner));
            Assert.Equal((400L, 600L), pool.Available(Volume(), 1000, 1000));
        }
    }

    [Fact]
    public void A_physical_pool_refuses_every_path_api_before_and_after_holding_debt()
    {
        var pool = PerceptualJobResourcePool.ForPhysicalVolumes();
        var path = Path.GetFullPath("volume-alias");
        var rejected = Guid.NewGuid();
        foreach (var restoreFirst in new[] { false, true }) {
            var owner = Guid.NewGuid();
            if (restoreFirst) Assert.True(pool.TryRestore(owner, Volume(), 600, 400));
            Assert.False(pool.TryRestore(rejected, path, 100, 100));
            Assert.False(pool.TryReserve(rejected, path, 100, 100, 1000, 1000));
            Assert.Null(pool.TryAcquire(path, 100, 100, 1000, 1000));
            Assert.Equal((0L, 0L), pool.Available(path, 1000, 1000));
            Assert.False(pool.Release(rejected));
            Assert.Equal(restoreFirst ? (400L, 600L) : (1000L, 1000L), pool.Available(Volume(), 1000, 1000));
            if (restoreFirst) Assert.True(pool.Release(owner));
        }
        Assert.False(pool.TryRestore(Guid.NewGuid(), path, 100, 100));
    }

    [Fact]
    public void A_path_pool_cannot_create_a_second_physical_namespace_or_change_mode_when_empty()
    {
        var pool = new PerceptualJobResourcePool();
        var path = Path.GetFullPath("volume-alias");
        var rejected = Guid.NewGuid();
        Assert.False(pool.TryRestore(rejected, Volume(), 600, 400));
        Assert.False(pool.TryReserve(rejected, Volume(), 600, 400, 1000, 1000));
        Assert.Null(pool.TryAcquire(Volume(), 600, 400, 1000, 1000));
        Assert.Equal((0L, 0L), pool.Available(Volume(), 1000, 1000));
        Assert.False(pool.Release(rejected));
        using var held = pool.TryAcquire(path, 600, 400, 1000, 1000);
        Assert.NotNull(held);
        Assert.Equal((400L, 600L), pool.Available(path, 1000, 1000));
        Assert.False(pool.TryRestore(rejected, Volume(), 1, 1));
    }

    [Fact]
    public void Physical_restoration_retains_debt_above_current_capacity_and_checks_overflow_before_mutation()
    {
        var pool = PerceptualJobResourcePool.ForPhysicalVolumes();
        var first = Guid.NewGuid();
        Assert.True(pool.TryRestore(first, Volume(), long.MaxValue, long.MaxValue));
        var rejected = Guid.NewGuid();
        Assert.False(pool.TryRestore(rejected, Volume(), 1, 1));
        Assert.False(pool.Release(rejected));
        Assert.Equal((0L, 0L), pool.Available(Volume(), 1000, 1000));
        Assert.True(pool.Release(first));
        Assert.Equal((1000L, 1000L), pool.Available(Volume(), 1000, 1000));
    }

    [Fact]
    public void Memory_is_shared_across_distinct_physical_volumes_and_disposable_and_restored_owners()
    {
        var pool = PerceptualJobResourcePool.ForPhysicalVolumes();
        var owner = Guid.NewGuid();
        Assert.True(pool.TryRestore(owner, Volume(), 600, 400));
        using var held = pool.TryAcquire(Volume("device:two"), 900, 500, 1000, 1000);
        Assert.NotNull(held);
        Assert.Equal((1000L, 100L), pool.Available(Volume("device:three"), 1000, 1000));
        Assert.Null(pool.TryAcquire(Volume("device:three"), 1, 101, 1000, 1000));
        Assert.True(pool.Release(owner));
        held.Dispose();
        held.Dispose();
        Assert.Equal((1000L, 1000L), pool.Available(Volume(), 1000, 1000));
    }

    [Fact]
    public void Concurrent_physical_owners_compete_for_the_same_capacity()
    {
        var pool = PerceptualJobResourcePool.ForPhysicalVolumes();
        var accepted = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        Parallel.For(0, 128, _ => {
            var owner = Guid.NewGuid();
            if (pool.TryReserve(owner, Volume(), 600, 100, 1000, 1000)) accepted.Add(owner);
        });
        var only = Assert.Single(accepted);
        Assert.Equal((400L, 900L), pool.Available(Volume(), 1000, 1000));
        Assert.True(pool.Release(only));
    }

    [Theory]
    [InlineData(0L, 0L, 1000L, 1000L)]
    [InlineData(-1L, 1L, 1000L, 1000L)]
    [InlineData(1L, -1L, 1000L, 1000L)]
    [InlineData(1L, 1L, null, 1000L)]
    [InlineData(1L, 1L, 1000L, null)]
    public void Invalid_charges_or_unknown_capacity_cannot_admit_physical_work(long disk, long memory, long? freeDisk, long? freeMemory)
    {
        var pool = PerceptualJobResourcePool.ForPhysicalVolumes();
        Assert.False(pool.TryReserve(Guid.NewGuid(), Volume(), disk, memory, freeDisk, freeMemory));
        Assert.False(pool.TryRestore(Guid.Empty, Volume(), 1, 1));
        Assert.False(pool.TryReserve(Guid.Empty, Volume(), 1, 1, 1000, 1000));
        Assert.Equal((1000L, 1000L), pool.Available(Volume(), 1000, 1000));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("device:\0other")]
    public void Invalid_physical_identifiers_are_refused_before_pool_use(string? invalid) =>
        Assert.ThrowsAny<ArgumentException>(() => new PhysicalVolumeId(invalid!));

    [Fact]
    public void An_unpaired_surrogate_is_rejected_without_replacing_its_bytes() =>
        Assert.ThrowsAny<ArgumentException>(() => new PhysicalVolumeId("device:" + (char)0xd800));

    [Fact]
    public void Identifier_bounds_use_strict_utf8_bytes_and_preserve_the_exact_value()
    {
        var exact = new string('\u00e9', 256);
        Assert.Equal(exact, new PhysicalVolumeId(exact).Value);
        Assert.Equal(new PhysicalVolumeId(exact), new PhysicalVolumeId(exact));
        Assert.ThrowsAny<ArgumentException>(() => new PhysicalVolumeId(exact + "a"));
        Assert.ThrowsAny<ArgumentException>(() => new PhysicalVolumeId(new('a', 513)));
    }
}
