using Optimisarr.Api.Replacement;

namespace Optimisarr.Tests;

public sealed class ReplacementCoordinatorTests
{
    [Fact]
    public async Task Finalisation_is_bounded_and_waiting_work_starts_when_a_slot_is_released()
    {
        var coordinator = new ReplacementCoordinator();
        Assert.True(await coordinator.TryBeginAsync(1, 101, CancellationToken.None));
        Assert.True(await coordinator.TryBeginAsync(2, 102, CancellationToken.None));
        Assert.True(coordinator.IsActive(1));

        var third = coordinator.TryBeginAsync(3, 103, CancellationToken.None);
        Assert.False(third.IsCompleted);
        Assert.False(coordinator.IsActive(3));
        Assert.Equal(ReplacementCoordinator.Capacity, coordinator.Active);
        Assert.Equal(1, coordinator.Waiting);

        coordinator.End(1, 101);
        Assert.True(await third);
        Assert.True(coordinator.IsActive(3));
        Assert.Equal(ReplacementCoordinator.Capacity, coordinator.Active);
        Assert.Equal(0, coordinator.Waiting);
        coordinator.End(2, 102);
        coordinator.End(3, 103);
        Assert.False(coordinator.IsActive(3));
    }

    [Fact]
    public async Task Cancelled_waiter_releases_its_source_claim()
    {
        var coordinator = new ReplacementCoordinator();
        Assert.True(await coordinator.TryBeginAsync(1, 101, CancellationToken.None));
        Assert.True(await coordinator.TryBeginAsync(2, 102, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        var third = coordinator.TryBeginAsync(3, 103, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => third);
        coordinator.End(1, 101);
        Assert.True(await coordinator.TryBeginAsync(4, 103, CancellationToken.None));
        coordinator.End(2, 102);
        coordinator.End(4, 103);
    }
}
