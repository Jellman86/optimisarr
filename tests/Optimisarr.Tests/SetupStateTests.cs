using Optimisarr.Core.Settings;

namespace Optimisarr.Tests;

public sealed class SetupStateTests
{
    [Fact]
    public void Fresh_install_starts_at_the_welcome_step()
    {
        var state = SetupState.Initialise(existing: null, databaseExistedBeforeStartup: false);

        Assert.False(state.Completed);
        Assert.Equal(0, state.CompletedStep);
        Assert.Equal(1, state.CurrentStep);
        Assert.Equal(SetupState.CurrentVersion, state.Version);
    }

    [Fact]
    public void Upgrade_without_setup_state_is_never_forced_through_the_wizard()
    {
        var state = SetupState.Initialise(existing: null, databaseExistedBeforeStartup: true);

        Assert.True(state.Completed);
        Assert.Equal(SetupState.StepCount, state.CompletedStep);
    }

    [Fact]
    public void Existing_progress_is_preserved_across_restart()
    {
        var existing = SetupState.Pending.Advance(1).Advance(2);

        var state = SetupState.Initialise(existing, databaseExistedBeforeStartup: false);

        Assert.Same(existing, state);
        Assert.Equal(3, state.CurrentStep);
    }

    [Fact]
    public void Steps_must_be_completed_in_order()
    {
        var state = SetupState.Pending;

        Assert.Throws<InvalidOperationException>(() => state.Advance(0));
        Assert.Throws<InvalidOperationException>(() => state.Advance(2));
        Assert.Throws<InvalidOperationException>(() => state.Advance(5));
        var progressed = state.Advance(1);
        Assert.Equal(1, progressed.CompletedStep);
        Assert.Same(progressed, progressed.Advance(1));
    }

    [Fact]
    public void Completion_requires_the_review_step_and_restart_is_explicit()
    {
        var state = SetupState.Pending.Advance(1).Advance(2).Advance(3).Advance(4);

        Assert.Throws<InvalidOperationException>(() => SetupState.Pending.Complete());
        var completed = state.Complete();
        Assert.True(completed.Completed);
        Assert.Equal(SetupState.StepCount, completed.CompletedStep);
        Assert.Equal(SetupState.Pending, completed.Restart());
    }
}
