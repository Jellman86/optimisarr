using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class DecodeHealthCheckTests
{
    [Fact]
    public void Decode_preserves_demuxer_clock_and_every_picture_without_repairing_input_timestamps()
    {
        var arguments = DecodeHealthCheck.BuildArguments("candidate ' [字幕].mkv");
        Assert.Contains("candidate ' [字幕].mkv", arguments);
        Assert.Contains("-map", arguments);
        Assert.Contains("0:V?", arguments);
        Assert.Contains("0:a?", arguments);
        var fps = Array.IndexOf(arguments, "-fps_mode:v");
        Assert.True(fps >= 0);
        Assert.Equal("passthrough", arguments[fps + 1]);
        var clock = Array.IndexOf(arguments, "-enc_time_base:v");
        Assert.True(clock >= 0);
        Assert.Equal("demux", arguments[clock + 1]);
        Assert.DoesNotContain("-r", arguments);
        Assert.DoesNotContain("-fflags", arguments);
        Assert.DoesNotContain("-err_detect", arguments);
        Assert.DoesNotContain("-ss", arguments);
        Assert.DoesNotContain("-t", arguments);
        Assert.Equal(new[] { "-f", "null", "-" }, arguments[^3..]);
    }
}
