using Optimisarr.Core.Settings;

namespace Optimisarr.Tests;

public sealed class DryRunUnlockEffectsTests
{
    private static readonly DryRunUnlockEffects Nothing = new(30, 0, 0, 0, 0, 0, 0);

    [Fact]
    public void Nothing_pending_needs_no_confirmation() => Assert.False(Nothing.NeedsConfirmation);

    [Fact]
    public void Each_thing_that_would_act_without_being_asked_needs_confirmation()
    {
        Assert.True((Nothing with { OriginalsToDelete = 1 }).NeedsConfirmation);
        Assert.True((Nothing with { DuplicatesToDelete = 1 }).NeedsConfirmation);
        Assert.True((Nothing with { LibrariesCleaningDuplicatesDaily = 1 }).NeedsConfirmation);
        Assert.True((Nothing with { JobsToReplace = 1 }).NeedsConfirmation);
    }
}
