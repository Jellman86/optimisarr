using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

public sealed class PerceptualJobResourcePoolTests
{
    private readonly string _volume = Path.GetFullPath("resource-pool-tests");

    [Fact]
    public void A_trailing_separator_cannot_hide_disk_debt()
    {
        var pool = new PerceptualJobResourcePool();
        Assert.NotNull(pool.TryAcquire(_volume + Path.DirectorySeparatorChar, 600, 400, 1000, 1000));

        Assert.Equal((400L, 600L), pool.Available(_volume, 1000, 1000));
        Assert.False(pool.TryReserve(Guid.NewGuid(), _volume, 501, 1, 1000, 1000));
    }

    [Fact]
    public void A_live_owner_cannot_reserve_the_same_capacity_twice()
    {
        var pool = new PerceptualJobResourcePool();
        var owner = Guid.NewGuid();
        Assert.True(pool.TryReserve(owner, _volume, 600, 400, 1000, 1000));

        Assert.False(pool.TryReserve(owner, _volume, 600, 400, 1000, 1000));
        Assert.Equal((400L, 600L), pool.Available(_volume, 1000, 1000));
        Assert.True(pool.Release(owner));
        Assert.False(pool.Release(owner));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    [InlineData(0, 0)]
    public void Invalid_charges_do_not_consume_capacity(long disk, long memory)
    {
        var pool = new PerceptualJobResourcePool();
        Assert.False(pool.TryReserve(Guid.NewGuid(), _volume, disk, memory, 1000, 1000));
        Assert.False(pool.TryReserve(Guid.Empty, _volume, 1, 1, 1000, 1000));
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
