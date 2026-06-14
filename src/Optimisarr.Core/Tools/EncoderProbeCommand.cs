namespace Optimisarr.Core.Tools;

/// <summary>
/// Builds a tiny throwaway ffmpeg encode that confirms a hardware encoder actually works on this
/// host — not merely that ffmpeg lists it. A few frames of a synthetic source are encoded to the
/// null muxer; a clean exit means the encoder opened and produced packets. The device-init and
/// upload arguments mirror what <c>FfmpegCommandBuilder</c> emits for each family, so a successful
/// probe means a real transcode for that encoder will also initialise. Pure; the service runs it.
/// </summary>
public static class EncoderProbeCommand
{
    // The conventional render node, matching FfmpegCommandBuilder's default.
    private const string RenderDevice = "/dev/dri/renderD128";

    public static IReadOnlyList<string> Build(string encoderName)
    {
        var isVaapi = encoderName.EndsWith("_vaapi", StringComparison.OrdinalIgnoreCase);
        var isQsv = encoderName.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase);

        var args = new List<string> { "-hide_banner", "-v", "error" };

        // Hardware device init must precede the input, as in a real transcode.
        if (isVaapi)
        {
            args.Add("-vaapi_device");
            args.Add(RenderDevice);
        }
        else if (isQsv)
        {
            args.Add("-init_hw_device");
            args.Add("qsv=hw");
            args.Add("-filter_hw_device");
            args.Add("hw");
        }

        // A short synthetic clip — enough frames to open the encoder and flush a packet. The
        // resolution must clear every encoder's minimum (NVENC HEVC/AV1 reject very small frames),
        // so 320x240 is used rather than a tiny thumbnail.
        args.AddRange(["-f", "lavfi", "-i", "color=c=black:s=320x240:r=25:d=0.2", "-frames:v", "3"]);

        if (isVaapi)
        {
            args.AddRange(["-vf", "format=nv12,hwupload"]);
        }
        else if (isQsv)
        {
            args.AddRange(["-vf", "hwupload=extra_hw_frames=64,format=qsv"]);
        }

        args.AddRange(["-c:v", encoderName, "-f", "null", "-"]);
        return args;
    }
}
