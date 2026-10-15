using Optimisarr.Api.Workers;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class WorkerCapabilityMappingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("{\"imageEncoders\":[\"../unsafe\"]}")]
    [InlineData("{\"audioMetricRevision\":\"unknown\"}")]
    public void Missing_or_corrupt_native_proof_does_not_break_legacy_capabilities(string? json)
    {
        var capabilities = new Worker { VideoEncoders = "libx265", PerceptualCapabilitiesJson = json }.ToCapabilities();
        Assert.Null(capabilities.Perceptual);
        Assert.Equal(["libx265"], capabilities.VideoEncoders);
    }
}
