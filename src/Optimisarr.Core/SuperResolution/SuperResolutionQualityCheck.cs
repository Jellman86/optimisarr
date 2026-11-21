namespace Optimisarr.Core.SuperResolution;

/// <summary>
/// How a library judges its upscaled results. VMAF measures closeness to the original, which an
/// upscaler changes on purpose: on real anime a good Real-CUGAN upscale scored about 72 on VMAF v1
/// where a plain re-encode scored 97. Every choice keeps the structural checks.
/// </summary>
public enum SuperResolutionQualityCheck
{
    /// <summary>VMAF is measured and recorded, and only its broken-frame floor applies.</summary>
    BrokenFramesOnly = 0,

    /// <summary>The library's normal VMAF gates apply; most upscales will be refused.</summary>
    FullGate = 1,

    /// <summary>No VMAF; only the structural checks apply.</summary>
    Off = 2,
}
