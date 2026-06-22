using Optimisarr.Core.Activity;

namespace Optimisarr.Tests;

public sealed class MediaTitleParserTests
{
    [Fact]
    public void Film_folder_with_year_yields_title_and_year()
    {
        var t = MediaTitleParser.Parse("adults/After Yang (2022)/After Yang (2022) WEBRip-2160p.mkv", isTv: false);
        Assert.Equal("After Yang", t!.Title);
        Assert.Equal(2022, t.Year);
    }

    [Fact]
    public void Film_remux_folder_parses_cleanly()
    {
        var t = MediaTitleParser.Parse("Film/Primer (2004)/Primer (2004) Remux-1080p.mkv", isTv: false);
        Assert.Equal("Primer", t!.Title);
        Assert.Equal(2004, t.Year);
    }

    [Fact]
    public void Scene_style_filename_without_a_folder_is_cleaned()
    {
        var t = MediaTitleParser.Parse("Movie.Name.2019.1080p.BluRay.x264.mkv", isTv: false);
        Assert.Equal("Movie Name", t!.Title);
        Assert.Equal(2019, t.Year);
    }

    [Fact]
    public void Tv_uses_the_show_folder_above_the_season()
    {
        var t = MediaTitleParser.Parse("tv/3 Body Problem/Season 1/3 Body Problem - S01E01 - Countdown WEBRip-1080p.mkv", isTv: true);
        Assert.Equal("3 Body Problem", t!.Title);
    }

    [Fact]
    public void Tv_without_a_season_folder_strips_the_episode_marker_from_the_name()
    {
        var t = MediaTitleParser.Parse("American Horror Story - S01E03 - Murder House.mkv", isTv: true);
        Assert.Equal("American Horror Story", t!.Title);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_path_returns_null(string? path)
    {
        Assert.Null(MediaTitleParser.Parse(path, isTv: false));
    }
}
