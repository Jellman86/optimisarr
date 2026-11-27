namespace Optimisarr.Sidecar.Core.Session;

/// <summary>How many jobs a Windows sidecar takes at once, from <c>OPTIMISARR_SIDECAR_CONCURRENCY</c>.</summary>
public static class SidecarConcurrency
{
    public const string Variable = "OPTIMISARR_SIDECAR_CONCURRENCY";
    public const int Maximum = 4;

    /// <summary>
    /// The configured number, or one with the reason when the setting cannot be used.
    ///
    /// <para>Anything unusable means one job, not the nearest allowed number. The Linux worker
    /// refuses to start on a bad value; a Windows service that refused would leave the machine
    /// silently offline until someone read the Event Log, so this one keeps working at the
    /// conservative default and says why. Rounding "8" down to four would hand the machine more
    /// simultaneous work than anyone actually chose.</para>
    /// </summary>
    public static (int Jobs, string? Problem) Parse(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return (1, null);
        }

        return int.TryParse(configured.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var jobs) && jobs is >= 1 and <= Maximum
            ? (jobs, null)
            : (1, $"{Variable} must be a whole number from 1 to {Maximum}; \"{configured}\" was ignored and one job at a time is used.");
    }
}
