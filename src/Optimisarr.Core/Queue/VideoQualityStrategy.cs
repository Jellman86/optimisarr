namespace Optimisarr.Core.Queue;

/// <summary>
/// Chooses whether a library uses its resolved encoder quality directly or spends a bounded
/// preparation pass selecting a per-title value from representative VMAF samples.
/// </summary>
public enum VideoQualityStrategy
{
    /// <summary>Use the library/profile quality and retain the single VMAF-only recovery retry.</summary>
    Fixed = 0,

    /// <summary>Select a bounded per-title quality before the full encode; final verification still applies.</summary>
    AdaptiveVmaf = 1
}
