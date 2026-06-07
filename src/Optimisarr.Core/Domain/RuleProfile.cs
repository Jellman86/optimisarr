namespace Optimisarr.Core.Domain;

/// <summary>
/// The optimisation rule profile applied to a library. Each profile maps to a
/// concrete set of eligibility and encoder settings (see <c>RuleProfileDefaults</c>).
/// </summary>
public enum RuleProfile
{
    /// <summary>Space saving with a safe, widely compatible HEVC target.</summary>
    ConservativeHevc = 0,

    /// <summary>Maximise device compatibility by targeting H.264.</summary>
    CompatibilityH264 = 1,

    /// <summary>Smallest files using AV1 where hardware/software allows.</summary>
    ExperimentalAv1 = 2,

    /// <summary>Remux/container cleanup only, no re-encode.</summary>
    RemuxCleanup = 3
}
