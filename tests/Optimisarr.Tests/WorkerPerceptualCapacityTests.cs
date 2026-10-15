using Optimisarr.Core.Domain;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

public sealed class WorkerPerceptualCapacityTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private static WorkerCapabilities Worker(long? memory, bool? ram) => new("linux", "x64", [], ["aac"], [],
        VmafCapability.None, 8 * GiB, 4, ProtocolVersion: 11, Perceptual: new(["libwebp"], true,
            AudioQualityResultParser.Revision, ImagePerceptualResultParser.Revision, memory, ram));

    [Theory]
    [InlineData(MediaKind.Image, 4L * GiB, false, true)]
    [InlineData(MediaKind.Image, 2L * GiB, false, false)]
    [InlineData(MediaKind.Image, 4L * GiB, true, false)]
    [InlineData(MediaKind.Image, 6L * GiB, true, true)]
    [InlineData(MediaKind.Audio, GiB, false, true)]
    [InlineData(MediaKind.Audio, GiB, true, false)]
    [InlineData(MediaKind.Audio, 3L * GiB, true, true)]
    [InlineData(MediaKind.Image, null, false, false)]
    [InlineData(MediaKind.Image, 6L * GiB, null, false)]
    public void Native_work_requires_peak_memory_and_counts_RAM_working_space_in_the_same_budget(MediaKind kind,
        long? memory, bool? ram, bool expected)
    {
        var wanted = new JobRequirements(null, kind == MediaKind.Audio ? "aac" : null, null, VmafCapability.None,
            2 * GiB, kind, kind == MediaKind.Image ? "libwebp" : null, PerceptualSearch: true);
        var match = WorkerCapabilityMatcher.Match(Worker(memory, ram), wanted);
        Assert.Equal(expected, match.Accepted);
        if (!expected) Assert.Contains(match.Reasons, reason => reason.Contains("memory", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    public void Invalid_or_overflowing_native_budgets_cannot_look_affordable(long scratch)
    {
        Assert.Null(PerceptualResourceBudget.MemoryBytes(MediaKind.Image, scratch, true));
    }

    [Fact]
    public void Legacy_audio_does_not_require_native_memory_proof()
    {
        var wanted = new JobRequirements(null, "aac", null, VmafCapability.None, GiB, MediaKind.Audio);
        Assert.True(WorkerCapabilityMatcher.Match(Worker(null, null) with { ProtocolVersion = 10, Perceptual = null }, wanted).Accepted);
    }

    [Fact]
    public void Negative_capacity_is_invalid_while_unknown_and_zero_are_explicit_values()
    {
        Assert.False(new WorkerPerceptualCapabilities(AvailableMemoryBytes: -1).Valid());
        Assert.True(new WorkerPerceptualCapabilities(AvailableMemoryBytes: 0, ScratchUsesMemory: true).Valid());
        Assert.True(new WorkerPerceptualCapabilities().Valid());
    }
}
