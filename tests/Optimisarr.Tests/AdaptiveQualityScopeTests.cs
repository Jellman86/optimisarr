using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class AdaptiveQualityScopeTests
{
    [Fact]
    public void Selected_quality_is_reused_by_the_encoder_that_proved_it() =>
        Assert.Equal(20, AdaptiveQualityScope.For(20, "hevc_qsv", "hevc_qsv"));

    [Fact]
    public void Encoder_names_compare_without_regard_to_case() =>
        Assert.Equal(20, AdaptiveQualityScope.For(20, "HEVC_QSV", "hevc_qsv"));

    [Theory]
    [InlineData("hevc_videotoolbox", "hevc_qsv")]
    [InlineData("hevc_qsv", "hevc_nvenc")]
    [InlineData("hevc_nvenc", "libx265")]
    [InlineData("hevc_qsv", null)]
    public void A_different_encoder_searches_again_instead_of_reusing_another_scale(string selectedOn, string? encoder) =>
        Assert.Null(AdaptiveQualityScope.For(20, selectedOn, encoder));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_quality_with_no_recorded_encoder_is_not_reused(string? selectedOn) =>
        Assert.Null(AdaptiveQualityScope.For(20, selectedOn, "hevc_qsv"));

    [Fact]
    public void No_selected_quality_stays_unselected() =>
        Assert.Null(AdaptiveQualityScope.For(null, "hevc_qsv", "hevc_qsv"));
}
