using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class ReferenceFrameAlignmentParserTests
{
    [Fact]
    public void Finds_the_first_accurate_frame_relative_to_the_preceding_keyframe()
    {
        const string frames = """
            1,280.280000
            0,280.989000
            0,281.031000
            0,281.072000
            """;

        var offset = ReferenceFrameAlignmentParser.Parse(frames, requestedStartSeconds: 281);

        Assert.NotNull(offset);
        Assert.Equal(0.751, offset.Value, precision: 3);
    }

    [Fact]
    public void Uses_the_last_keyframe_before_the_requested_frame()
    {
        const string frames = """
            1,276.276000
            1,280.280000
            0,281.031000
            """;

        Assert.Equal(0.751, ReferenceFrameAlignmentParser.Parse(frames, 281)!.Value, precision: 3);
    }

    [Theory]
    [InlineData("0,281.031000")]
    [InlineData("1,280.280000\n0,280.989000")]
    [InlineData("not,csv")]
    public void Fails_closed_without_both_alignment_boundaries(string frames)
    {
        Assert.Null(ReferenceFrameAlignmentParser.Parse(frames, requestedStartSeconds: 281));
    }
}
