using Optimisarr.Core.Tools;

namespace Optimisarr.Tests;

public sealed class EncoderProbeCommandTests
{
    private static int IndexOf(IReadOnlyList<string> args, string value) =>
        ((List<string>)args).IndexOf(value);

    [Fact]
    public void Cpu_encoder_probe_is_a_minimal_null_encode()
    {
        var args = EncoderProbeCommand.Build("libx265");

        Assert.Equal("libx265", args[IndexOf(args, "-c:v") + 1]);
        // The encode is written to the null muxer: "… -f null -".
        Assert.Equal(["-f", "null", "-"], args.TakeLast(3));
        // No hardware device init for a software encoder.
        Assert.DoesNotContain("-vaapi_device", args);
        Assert.DoesNotContain("-init_hw_device", args);
    }

    [Fact]
    public void Nvenc_probe_needs_no_device_init()
    {
        var args = EncoderProbeCommand.Build("hevc_nvenc");

        Assert.Equal("hevc_nvenc", args[IndexOf(args, "-c:v") + 1]);
        Assert.DoesNotContain("-vaapi_device", args);
        Assert.DoesNotContain("-init_hw_device", args);
    }

    [Fact]
    public void Vaapi_probe_inits_the_device_before_input_and_uploads_frames()
    {
        var args = EncoderProbeCommand.Build("hevc_vaapi");

        var deviceIndex = IndexOf(args, "-vaapi_device");
        Assert.Equal("/dev/dri/renderD128", args[deviceIndex + 1]);
        Assert.True(deviceIndex < IndexOf(args, "-i"));
        Assert.Contains("format=nv12,hwupload", args[IndexOf(args, "-vf") + 1]);
        Assert.Equal("hevc_vaapi", args[IndexOf(args, "-c:v") + 1]);
    }

    [Fact]
    public void Qsv_probe_inits_the_device_and_uploads_frames()
    {
        var args = EncoderProbeCommand.Build("hevc_qsv");

        var initIndex = IndexOf(args, "-init_hw_device");
        Assert.Equal("qsv=hw", args[initIndex + 1]);
        Assert.True(initIndex < IndexOf(args, "-i"));
        Assert.Contains("format=qsv", args[IndexOf(args, "-vf") + 1]);
        Assert.Equal("hevc_qsv", args[IndexOf(args, "-c:v") + 1]);
    }

    private static int IndexOf(IReadOnlyList<string> args, string value, int start) =>
        ((List<string>)args).IndexOf(value, start);
}
