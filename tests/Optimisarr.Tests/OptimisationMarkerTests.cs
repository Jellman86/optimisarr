using Optimisarr.Core;

namespace Optimisarr.Tests;

public sealed class OptimisationMarkerTests
{
    [Fact]
    public void Image_software_round_trips_the_marker_value()
    {
        var software = OptimisationMarker.FormatImageSoftware("0.5.0");

        Assert.Equal("optimisarr/0.5.0", software);
        Assert.Equal("0.5.0", OptimisationMarker.TryParseImageSoftware(software));
    }

    [Fact]
    public void Parsing_is_case_insensitive_on_the_prefix()
    {
        Assert.Equal("1.2.3", OptimisationMarker.TryParseImageSoftware("Optimisarr/1.2.3"));
    }

    [Theory]
    [InlineData("Adobe Photoshop 2026")]
    [InlineData("GIMP 2.10")]
    [InlineData("")]
    [InlineData(null)]
    public void A_foreign_or_missing_software_field_is_not_a_marker(string? software)
    {
        Assert.Null(OptimisationMarker.TryParseImageSoftware(software));
    }
}
