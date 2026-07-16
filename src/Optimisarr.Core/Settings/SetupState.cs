namespace Optimisarr.Core.Settings;

/// <summary>
/// Durable progress through the five-step first-run setup. The completed-step cursor makes refresh
/// and restart resumable; upgraded installations are marked complete so a new feature never blocks
/// an already-running deployment.
/// </summary>
public sealed record SetupState(int Version, int CompletedStep, bool Completed)
{
    public const int CurrentVersion = 1;
    public const int StepCount = 5;

    public static readonly SetupState Pending = new(CurrentVersion, 0, Completed: false);
    public static readonly SetupState CompletedUpgrade = new(CurrentVersion, StepCount, Completed: true);

    public int CurrentStep => Completed ? StepCount : Math.Min(CompletedStep + 1, StepCount);

    public static SetupState Initialise(SetupState? existing, bool databaseExistedBeforeStartup) =>
        existing ?? (databaseExistedBeforeStartup ? CompletedUpgrade : Pending);

    public SetupState Advance(int completedStep)
    {
        if (Completed)
        {
            return this;
        }

        if (completedStep is < 1 or >= StepCount)
        {
            throw new InvalidOperationException($"Setup step {completedStep} is outside the resumable step range.");
        }

        if (completedStep <= CompletedStep)
        {
            return this;
        }

        if (completedStep != CurrentStep)
        {
            throw new InvalidOperationException($"Setup step {completedStep} cannot follow completed step {CompletedStep}.");
        }

        return this with { CompletedStep = completedStep };
    }

    public SetupState Complete()
    {
        if (!Completed && CompletedStep != StepCount - 1)
        {
            throw new InvalidOperationException("Setup can be completed only from the final review step.");
        }

        return this with { CompletedStep = StepCount, Completed = true };
    }

    public SetupState Restart() => Pending;
}
