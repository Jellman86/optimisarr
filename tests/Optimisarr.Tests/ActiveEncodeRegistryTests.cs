using Optimisarr.Api.Realtime;

namespace Optimisarr.Tests;

public sealed class ActiveEncodeRegistryTests
{
    [Fact]
    public void Verification_tracking_flags_active_work_until_disposed()
    {
        var registry = new ActiveEncodeRegistry();
        Assert.False(registry.VerificationInProgress);

        var tracking = registry.TrackVerification();
        Assert.True(registry.VerificationInProgress);
        // Verification does not register an encode process, so it must not look like an encode.
        Assert.Equal(0, registry.Count);

        tracking.Dispose();
        Assert.False(registry.VerificationInProgress);
    }

    [Fact]
    public void Verification_tracking_is_reference_counted()
    {
        var registry = new ActiveEncodeRegistry();

        var first = registry.TrackVerification();
        var second = registry.TrackVerification();
        Assert.True(registry.VerificationInProgress);

        first.Dispose();
        Assert.True(registry.VerificationInProgress);

        second.Dispose();
        Assert.False(registry.VerificationInProgress);
    }
}
