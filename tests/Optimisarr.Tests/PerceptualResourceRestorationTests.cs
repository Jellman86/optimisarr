using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

public sealed class PerceptualResourceRestorationTests
{
    private readonly string _volume = Path.GetFullPath("restored-resource-tests");

    [Fact]
    public void Restored_debt_is_retained_even_when_current_capacity_is_below_the_original_charge()
    {
        var pool = new PerceptualJobResourcePool();
        var owner = Guid.NewGuid();

        Assert.True(pool.TryRestore(owner, _volume, 600, 400));
        Assert.Equal((0L, 0L), pool.Available(_volume, 100, 100));
        Assert.False(pool.TryReserve(Guid.NewGuid(), _volume, 1, 1, 600, 400));
        Assert.True(pool.Release(owner));
        Assert.Equal((100L, 100L), pool.Available(_volume, 100, 100));
    }

    [Fact]
    public void Replaying_the_same_owner_and_charge_is_idempotent()
    {
        var pool = new PerceptualJobResourcePool();
        var owner = Guid.NewGuid();

        Assert.True(pool.TryRestore(owner, _volume, 600, 400));
        Assert.True(pool.TryRestore(owner, Path.Combine(_volume, "."), 600, 400));
        Assert.Equal((400L, 600L), pool.Available(_volume, 1000, 1000));
        Assert.True(pool.Release(owner));
        Assert.False(pool.Release(owner));
        Assert.Equal((1000L, 1000L), pool.Available(_volume, 1000, 1000));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_trailing_separator_cannot_hide_ordinary_or_restored_disk_debt(bool restored)
    {
        var pool = new PerceptualJobResourcePool();
        var alternate = _volume + Path.DirectorySeparatorChar;
        if (restored) Assert.True(pool.TryRestore(Guid.NewGuid(), alternate, 600, 400));
        else Assert.NotNull(pool.TryAcquire(alternate, 600, 400, 1000, 1000));

        Assert.Equal((400L, 600L), pool.Available(_volume, 1000, 1000));
        Assert.False(pool.TryReserve(Guid.NewGuid(), _volume, 501, 1, 1000, 1000));
    }

    [Fact]
    public void A_trailing_separator_alias_is_the_same_owner_footprint_during_replay()
    {
        var pool = new PerceptualJobResourcePool();
        var owner = Guid.NewGuid();
        Assert.True(pool.TryRestore(owner, _volume, 600, 400));

        Assert.True(pool.TryRestore(owner, _volume + Path.DirectorySeparatorChar, 600, 400));

        Assert.Equal((400L, 600L), pool.Available(_volume, 1000, 1000));
    }

    [Theory]
    [InlineData("other-volume", 600, 400)]
    [InlineData("same-volume", 601, 400)]
    [InlineData("same-volume", 600, 401)]
    public void A_conflicting_owner_replay_does_not_change_any_existing_charge(string volume, long disk, long memory)
    {
        var pool = new PerceptualJobResourcePool();
        var owner = Guid.NewGuid();
        Assert.True(pool.TryRestore(owner, _volume, 600, 400));

        Assert.False(pool.TryRestore(owner, volume == "same-volume" ? _volume : _volume + "-other", disk, memory));

        Assert.Equal((400L, 600L), pool.Available(_volume, 1000, 1000));
        Assert.Equal((1000L, 600L), pool.Available(_volume + "-other", 1000, 1000));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    [InlineData(0, 0)]
    public void Invalid_restore_charges_do_not_consume_capacity(long disk, long memory)
    {
        var pool = new PerceptualJobResourcePool();
        Assert.False(pool.TryRestore(Guid.NewGuid(), _volume, disk, memory));
        Assert.False(pool.TryRestore(Guid.Empty, _volume, 1, 1));
        Assert.False(pool.TryReserve(Guid.Empty, _volume, 1, 1, 1000, 1000));
        Assert.Equal((1000L, 1000L), pool.Available(_volume, 1000, 1000));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Overflow_refuses_the_entire_restore_without_partially_charging_another_resource(bool memoryOverflow)
    {
        var pool = new PerceptualJobResourcePool();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.True(pool.TryRestore(first, _volume, memoryOverflow ? 0 : long.MaxValue, memoryOverflow ? long.MaxValue : 0));

        Assert.False(pool.TryRestore(second, _volume, 1, 1));

        Assert.False(pool.Release(second));
        Assert.Equal(memoryOverflow ? (100L, 0L) : (0L, 100L), pool.Available(_volume, 100, 100));
        Assert.True(pool.Release(first));
        Assert.Equal((100L, 100L), pool.Available(_volume, 100, 100));
    }

    [Fact]
    public void A_live_owner_cannot_reserve_the_same_capacity_twice()
    {
        var pool = new PerceptualJobResourcePool();
        var owner = Guid.NewGuid();
        Assert.True(pool.TryReserve(owner, _volume, 600, 400, 1000, 1000));

        Assert.False(pool.TryReserve(owner, _volume, 600, 400, 1000, 1000));
        Assert.True(pool.TryRestore(owner, _volume, 600, 400));
        Assert.Equal((400L, 600L), pool.Available(_volume, 1000, 1000));
        Assert.True(pool.Release(owner));
    }

    [Fact]
    public void Restored_and_ordinary_reservations_share_the_same_budget_and_release_only_their_own_charge()
    {
        var pool = new PerceptualJobResourcePool();
        var restored = Guid.NewGuid();
        Assert.True(pool.TryRestore(restored, _volume, 600, 400));
        using var ordinary = pool.TryAcquire(_volume, 300, 500, 1000, 1000);
        Assert.NotNull(ordinary);
        Assert.Null(pool.TryAcquire(_volume, 101, 1, 1000, 1000));
        Assert.Equal((100L, 100L), pool.Available(_volume, 1000, 1000));

        ordinary.Dispose(); ordinary.Dispose();

        Assert.Equal((400L, 600L), pool.Available(_volume, 1000, 1000));
        Assert.True(pool.Release(restored));
        Assert.Equal((1000L, 1000L), pool.Available(_volume, 1000, 1000));
    }

    [Fact]
    public void Changing_the_volume_does_not_hide_restored_native_memory_debt()
    {
        var pool = new PerceptualJobResourcePool();
        Assert.True(pool.TryRestore(Guid.NewGuid(), _volume, 600, 400));

        Assert.Equal((1000L, 600L), pool.Available(_volume + "-new", 1000, 1000));
        Assert.False(pool.TryReserve(Guid.NewGuid(), _volume + "-new", 1, 601, 1000, 1000));
    }

    [Fact]
    public void Concurrent_reconciliation_cannot_double_restore_or_double_release_an_owner()
    {
        var pool = new PerceptualJobResourcePool();
        var owner = Guid.NewGuid();
        Parallel.For(0, 128, _ => Assert.True(pool.TryRestore(owner, _volume, 600, 400)));
        Assert.Equal((400L, 600L), pool.Available(_volume, 1000, 1000));
        var releases = 0;
        Parallel.For(0, 128, _ => { if (pool.Release(owner)) Interlocked.Increment(ref releases); });
        Assert.Equal(1, releases);
        Assert.Equal((1000L, 1000L), pool.Available(_volume, 1000, 1000));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Concurrent_distinct_owners_cannot_over_reserve_either_resource(bool limitedByMemory)
    {
        var pool = new PerceptualJobResourcePool();
        var accepted = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        Parallel.For(0, 128, _ =>
        {
            var owner = Guid.NewGuid();
            if (pool.TryReserve(owner, _volume, limitedByMemory ? 100 : 600, limitedByMemory ? 600 : 100, 1000, 1000))
                accepted.Add(owner);
        });

        var admitted = Assert.Single(accepted);
        Assert.Equal(limitedByMemory ? (900L, 400L) : (400L, 900L), pool.Available(_volume, 1000, 1000));
        Assert.True(pool.Release(admitted));
        Assert.Equal((1000L, 1000L), pool.Available(_volume, 1000, 1000));
    }
}
