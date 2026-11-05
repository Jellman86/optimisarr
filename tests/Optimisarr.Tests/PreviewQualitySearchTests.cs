using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class PreviewQualitySearchTests
{
    [Theory]
    [InlineData(MediaKind.Audio, true, false, false)]
    [InlineData(MediaKind.Image, false, true, false)]
    [InlineData(MediaKind.Video, false, false, true)]
    public void A_preview_says_it_uses_the_saved_setting_when_its_kind_would_search(
        MediaKind kind, bool audio, bool image, bool soundtrack) =>
        Assert.True(PreviewQualitySearch.UsesSavedSettingInstead(kind, audio, image, soundtrack));

    [Theory]
    [InlineData(MediaKind.Audio, false, true, true)]
    [InlineData(MediaKind.Image, true, false, true)]
    [InlineData(MediaKind.Video, true, true, false)]
    [InlineData(MediaKind.Unknown, true, true, true)]
    public void A_preview_says_nothing_when_its_kind_has_no_search_to_skip(
        MediaKind kind, bool audio, bool image, bool soundtrack) =>
        Assert.False(PreviewQualitySearch.UsesSavedSettingInstead(kind, audio, image, soundtrack));
}
