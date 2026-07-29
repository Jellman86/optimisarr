namespace Optimisarr.Core.Queue;

/// <summary>Chooses when an HDR-to-SDR job may use a hardware colour transform.</summary>
public static class HardwareToneMapPolicy
{
    /// <summary>
    /// Hardware tone mapping currently requires a freshly confirmed HDR10/PQ source and an
    /// end-to-end QSV or VA-API surface path. FFmpeg documents tonemap_vaapi as HDR10-only, so HLG,
    /// Dolby Vision, and unknown transfer metadata stay on the compatible software transform.
    /// VMAF-gated jobs also keep that software transform so the reference receives the exact same
    /// colour conversion before comparison.
    /// </summary>
    public static bool ShouldUse(
        HdrToneMapMode configuredMode,
        bool hardwareDecode,
        bool toneMapToSdr,
        bool vmafQualityGateEnabled,
        bool sourceIsDolbyVision,
        string? sourceColorTransfer,
        string? videoEncoder) =>
        configuredMode == HdrToneMapMode.Hardware
        && hardwareDecode
        && toneMapToSdr
        && !vmafQualityGateEnabled
        && !sourceIsDolbyVision
        && string.Equals(sourceColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase)
        && (videoEncoder?.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase) == true
            || videoEncoder?.EndsWith("_vaapi", StringComparison.OrdinalIgnoreCase) == true);
}
