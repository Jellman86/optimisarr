using Optimisarr.Core.Tools;

namespace Optimisarr.Tests;

public sealed class FfmpegFilterParserTests
{
    [Fact]
    public void Finds_the_exact_libvmaf_filter()
    {
        const string filters = """
             ... vmafmotion        V->V       Calculate the VMAF Motion score.
             ..C libvmaf           VV->V      Calculate the VMAF between two video streams.
            """;

        Assert.True(FfmpegFilterParser.Contains(filters, "libvmaf"));
    }

    [Fact]
    public void Does_not_mistake_vmafmotion_for_libvmaf()
    {
        const string filters = """
             ... vmafmotion        V->V       Calculate the VMAF Motion score.
            """;

        Assert.False(FfmpegFilterParser.Contains(filters, "libvmaf"));
    }
}
