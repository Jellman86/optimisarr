using Optimisarr.Api.Queue;

namespace Optimisarr.Tests;

public sealed class AdaptiveQualityScratchTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "optimisarr-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Sample_cleanup_preserves_the_workspace_for_the_next_window()
    {
        Directory.CreateDirectory(_root);
        var firstSample = Path.Combine(_root, "q20-window0.mp4");
        File.WriteAllText(firstSample, "sample");

        AdaptiveQualityScratch.DeleteSample(firstSample);

        Assert.False(File.Exists(firstSample));
        Assert.True(Directory.Exists(_root));

        var nextSample = Path.Combine(_root, "q20-window1.mp4");
        File.WriteAllText(nextSample, "next");
        Assert.True(File.Exists(nextSample));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
