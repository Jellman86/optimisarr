using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class FfmpegLogBufferTests
{
    [Fact]
    public void Empty_buffer_renders_as_null()
    {
        Assert.Null(new FfmpegLogBuffer().ToLog());
    }

    [Fact]
    public void Keeps_every_line_when_under_the_limits()
    {
        var buffer = new FfmpegLogBuffer(headLimit: 5, tailLimit: 5);
        buffer.Append("Input #0");
        buffer.Append("Stream mapping");
        buffer.Append("Conversion failed!");

        Assert.Equal("Input #0\nStream mapping\nConversion failed!", buffer.ToLog());
    }

    [Fact]
    public void Elides_the_middle_but_keeps_head_and_tail_when_long()
    {
        var buffer = new FfmpegLogBuffer(headLimit: 2, tailLimit: 2);
        for (var i = 1; i <= 10; i++)
        {
            buffer.Append($"line {i}");
        }

        var log = buffer.ToLog()!;
        // First two and last two survive; the six in the middle are elided with a count.
        Assert.StartsWith("line 1\nline 2\n", log);
        Assert.EndsWith("\nline 9\nline 10", log);
        Assert.Contains("6 lines elided", log);
    }
}
