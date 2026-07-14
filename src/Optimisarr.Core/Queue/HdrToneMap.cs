namespace Optimisarr.Core.Queue;

/// <summary>
/// The single production HDR-to-SDR transform. Transcoding and quality-reference
/// preparation share it so VMAF compares an SDR output with an equivalently
/// tone-mapped reference rather than comparing unlike transfer functions.
/// </summary>
public static class HdrToneMap
{
    public const string Filter =
        "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable," +
        "zscale=t=bt709:m=bt709:r=tv,format=yuv420p";
}
