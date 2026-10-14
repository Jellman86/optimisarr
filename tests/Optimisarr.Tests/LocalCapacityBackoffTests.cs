using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class LocalCapacityBackoffTests
{
    [Fact]
    public void Capacity_refusal_holds_only_the_same_attempt_for_thirty_monotonic_seconds()
    {
        var clock = new Clock(); var backoff = new LocalCapacityBackoff(clock);
        backoff.Hold(1, 3);
        Assert.True(backoff.IsHeld(1, 3));
        Assert.False(backoff.IsHeld(2, 3));
        Assert.False(backoff.IsHeld(1, 4));
        clock.Now = DateTimeOffset.MinValue;
        clock.Ticks += 29;
        Assert.True(backoff.IsHeld(1, 3));
        clock.Now = DateTimeOffset.MaxValue;
        clock.Ticks++;
        Assert.False(backoff.IsHeld(1, 3));
    }

    [Fact]
    public void A_late_old_refusal_cannot_replace_the_newer_attempts_hold()
    {
        var clock = new Clock(); var backoff = new LocalCapacityBackoff(clock);
        backoff.Hold(1, 4); clock.Ticks += 10; backoff.Hold(1, 3);
        Assert.True(backoff.IsHeld(1, 4));
        Assert.False(backoff.IsHeld(1, 3));
        clock.Ticks += 20;
        backoff.PruneExpired();
        Assert.False(backoff.IsHeld(1, 4));
    }

    private sealed class Clock : TimeProvider
    {
        public long Ticks;
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Ticks;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
