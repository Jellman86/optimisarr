namespace Optimisarr.Core.Queue;

/// <summary>
/// The single production HDR-to-SDR transform. Transcoding and quality-reference
/// preparation share it so VMAF compares an SDR output with an equivalently
/// tone-mapped reference rather than comparing unlike transfer functions.
/// </summary>
public static class HdrToneMap
{
    public const string SoftwareFilter =
        "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable," +
        "zscale=t=bt709:m=bt709:r=tv,format=yuv420p";

    public const string QsvFilter =
        "vpp_qsv=tonemap=1:format=nv12:out_range=tv:out_color_matrix=bt709:" +
        "out_color_primaries=bt709:out_color_transfer=bt709";

    public const string VaapiFilter =
        "tonemap_vaapi=format=nv12:matrix=bt709:primaries=bt709:transfer=bt709";

    // Kept as the stable name for the software reference path used by VMAF.
    public const string Filter = SoftwareFilter;
}
