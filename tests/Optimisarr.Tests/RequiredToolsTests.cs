using Optimisarr.Core.Tools;

namespace Optimisarr.Tests;

public sealed class RequiredToolsTests
{
    private static ToolCheckResult Tool(string name, bool available, bool required) =>
        new(name, name.ToLowerInvariant(), available, required, available ? "1.0" : null, available ? null : "not found");

    [Fact]
    public void Missing_names_each_required_tool_that_did_not_run()
    {
        var missing = RequiredTools.Missing([
            Tool("FFmpeg", available: false, required: true),
            Tool("FFmpeg (VMAF)", available: false, required: false),
            Tool("ffprobe", available: false, required: true),
        ]);

        Assert.Equal(["FFmpeg", "ffprobe"], missing);
    }

    [Fact]
    public void Missing_is_empty_when_only_an_optional_tool_is_absent()
    {
        var missing = RequiredTools.Missing([
            Tool("FFmpeg", available: true, required: true),
            Tool("FFmpeg (VMAF)", available: false, required: false),
            Tool("ffprobe", available: true, required: true),
        ]);

        Assert.Empty(missing);
    }
}
