using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class ImageComparisonReferenceServiceTests
{
    [Fact]
    public void Reference_command_decodes_one_frame_to_lossless_png_without_a_shell()
    {
        var args = ImageComparisonReferenceService.BuildArguments(
            "/data/photos/family trip.tiff",
            "/work/calibration/reference.png");

        Assert.Contains("-nostdin", args);
        Assert.Contains("-y", args);
        Assert.Equal("/data/photos/family trip.tiff", args[IndexOf(args, "-i") + 1]);
        Assert.Equal("0:v:0", args[IndexOf(args, "-map") + 1]);
        Assert.Equal("1", args[IndexOf(args, "-frames:v") + 1]);
        Assert.Equal("png", args[IndexOf(args, "-c:v") + 1]);
        Assert.Equal("/work/calibration/reference.png", args[^1]);
    }

    private static int IndexOf(IReadOnlyList<string> args, string value) =>
        args.ToList().IndexOf(value);
}
