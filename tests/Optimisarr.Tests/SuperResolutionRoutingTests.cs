using Optimisarr.Core.Domain;
using Optimisarr.Core.SuperResolution;

namespace Optimisarr.Tests;

/// <summary>
/// The experimental switch is the one gate for super-resolution: with it off, an opted-in library
/// behaves exactly like any other library, and previews or calibration samples never take the
/// engine path even when it is on.
/// </summary>
public sealed class SuperResolutionRoutingTests
{
    [Fact]
    public void Super_resolution_applies_only_to_a_replace_bound_video_job_of_an_opted_in_library_with_the_feature_on()
    {
        Assert.True(SuperResolutionRouting.Applies(featureEnabled: true, libraryOptedIn: true, MediaKind.Video, replaceBound: true));
    }

    [Theory]
    [InlineData(false, true, MediaKind.Video, true)]
    [InlineData(true, false, MediaKind.Video, true)]
    [InlineData(true, true, MediaKind.Audio, true)]
    [InlineData(true, true, MediaKind.Image, true)]
    [InlineData(true, true, MediaKind.Video, false)]
    public void Super_resolution_does_not_apply_when_any_condition_is_missing(
        bool featureEnabled, bool libraryOptedIn, MediaKind kind, bool replaceBound)
    {
        Assert.False(SuperResolutionRouting.Applies(featureEnabled, libraryOptedIn, kind, replaceBound));
    }
}
