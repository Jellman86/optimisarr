namespace Optimisarr.Core.Verification;

/// <summary>Digital silence is a measured zero peak, represented without a fabricated finite dB value.</summary>
public static class TruePeakSafety
{
    public static bool Known(double? dbtp, bool silent) => silent ? dbtp is null : dbtp is { } value && double.IsFinite(value);

    public static bool Introduced(double? source, bool sourceSilent, double? candidate, bool candidateSilent, double ceiling)
    {
        if (!Known(source, sourceSilent) || !Known(candidate, candidateSilent) || !double.IsFinite(ceiling))
            throw new ArgumentException("Complete true-peak measurements and a finite ceiling are required.");
        if (candidateSilent) return false;
        // Preserve the existing 0.1 dB measurement margin and worsening rule for
        // already-hot sources. Samples and final verification use one decision.
        return candidate!.Value > ceiling && (sourceSilent || candidate.Value > source!.Value + 0.1);
    }
}
