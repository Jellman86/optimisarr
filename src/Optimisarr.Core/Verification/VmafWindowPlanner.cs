namespace Optimisarr.Core.Verification;

/// <summary>A time window scored by VMAF; <see cref="Full"/> means the complete file.</summary>
public sealed record VmafWindow(int? StartSeconds, int? DurationSeconds)
{
    public static VmafWindow Full { get; } = new(null, null);
}

/// <summary>Plans deterministic early, middle and late samples for long videos.</summary>
public static class VmafWindowPlanner
{
    private const int WindowSeconds = 40;
    private const int MinimumSampledDurationSeconds = 150;

    public static IReadOnlyList<VmafWindow> Plan(double durationSeconds, bool enabled)
    {
        if (!enabled || durationSeconds <= MinimumSampledDurationSeconds)
        {
            return [VmafWindow.Full];
        }

        var lastStart = Math.Max(0, (int)Math.Floor(durationSeconds) - WindowSeconds);
        int Centred(double fraction) => Math.Clamp(
            (int)Math.Floor(durationSeconds * fraction) - (WindowSeconds / 2), 0, lastStart);

        return [
            new VmafWindow(Centred(0.1), WindowSeconds),
            new VmafWindow(Centred(0.5), WindowSeconds),
            new VmafWindow(Centred(0.9), WindowSeconds)
        ];
    }

    /// <summary>
    /// Plans the bounded samples used to choose an adaptive encode quality. Unlike final clip-VMAF,
    /// this never expands a short title into an unbounded full-file pass. Overlapping positions are
    /// de-duplicated, so a title shorter than one sample is measured once.
    /// </summary>
    public static IReadOnlyList<VmafWindow> PlanAdaptive(double durationSeconds)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
        {
            return [];
        }

        var duration = Math.Max(1, (int)Math.Floor(durationSeconds));
        var windowSeconds = Math.Min(WindowSeconds, duration);
        var lastStart = Math.Max(0, duration - windowSeconds);
        int Centred(double fraction) => Math.Clamp(
            (int)Math.Floor(durationSeconds * fraction) - (windowSeconds / 2), 0, lastStart);

        return new[] { Centred(0.1), Centred(0.5), Centred(0.9) }
            .Distinct()
            .Select(start => new VmafWindow(start, windowSeconds))
            .ToList();
    }
}
