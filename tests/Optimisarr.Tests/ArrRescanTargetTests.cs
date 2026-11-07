using Optimisarr.Core.Activity;

namespace Optimisarr.Tests;

public sealed class ArrRescanTargetTests
{
    [Fact]
    public void Finds_the_movie_whose_folder_holds_the_file_even_under_a_different_mount()
    {
        var titles = new[] { new ArrTitleFolder(7, "/movies/Heat (1995)"), new ArrTitleFolder(8, "/movies/Ronin (1998)") };

        Assert.Equal(7, ArrRescanTarget.TitleFor("/media/films/Heat (1995)/Heat (1995).mp4", titles, ArrRescanTarget.MovieFolderDepth));
    }

    [Fact]
    public void Finds_the_series_from_an_episode_inside_a_season_folder()
    {
        var titles = new[] { new ArrTitleFolder(3, "/tv/The Wire"), new ArrTitleFolder(4, "/tv/Taxi") };

        Assert.Equal(3, ArrRescanTarget.TitleFor("/data/tv/The Wire/Season 01/The Wire - S01E01.mkv", titles, ArrRescanTarget.SeriesFolderDepth));
    }

    [Fact]
    public void Folder_names_compare_like_the_tracked_file_matcher_does()
    {
        var titles = new[] { new ArrTitleFolder(5, "D:\\Movies\\Amélie (2001)") };

        Assert.Equal(5, ArrRescanTarget.TitleFor("/media/movies/AME\u0301LIE (2001)/Amélie.mkv", titles, ArrRescanTarget.MovieFolderDepth));
    }

    [Fact]
    public void Two_titles_with_the_same_folder_name_are_told_apart_by_their_parent_folders()
    {
        var titles = new[] { new ArrTitleFolder(1, "/movies/hd/Heat (1995)"), new ArrTitleFolder(2, "/movies/uhd/Heat (1995)") };

        Assert.Equal(2, ArrRescanTarget.TitleFor("/media/uhd/Heat (1995)/Heat.mkv", titles, ArrRescanTarget.MovieFolderDepth));
    }

    [Fact]
    public void Two_titles_that_cannot_be_told_apart_give_no_answer()
    {
        var titles = new[] { new ArrTitleFolder(1, "/a/Heat (1995)"), new ArrTitleFolder(2, "/b/Heat (1995)") };

        Assert.Null(ArrRescanTarget.TitleFor("/media/c/Heat (1995)/Heat.mkv", titles, ArrRescanTarget.MovieFolderDepth));
    }

    [Fact]
    public void A_file_outside_every_title_folder_gives_no_answer()
    {
        var titles = new[] { new ArrTitleFolder(1, "/movies/Heat (1995)") };

        Assert.Null(ArrRescanTarget.TitleFor("/media/movies/Ronin (1998)/Ronin.mkv", titles, ArrRescanTarget.MovieFolderDepth));
    }

    [Fact]
    public void The_file_name_itself_never_matches_a_title_folder()
    {
        var titles = new[] { new ArrTitleFolder(1, "/movies/Heat.mkv") };

        Assert.Null(ArrRescanTarget.TitleFor("/media/loose/Heat.mkv", titles, ArrRescanTarget.MovieFolderDepth));
    }

    [Fact]
    public void A_folder_inside_a_series_that_shares_another_series_name_gives_no_answer()
    {
        var titles = new[] { new ArrTitleFolder(1, "/tv/The Wire"), new ArrTitleFolder(2, "/tv/Specials") };

        Assert.Null(ArrRescanTarget.TitleFor("/tv/The Wire/Specials/The Wire - S00E01.mkv", titles, ArrRescanTarget.SeriesFolderDepth));
    }

    [Fact]
    public void A_library_root_that_shares_a_title_name_is_never_matched()
    {
        var titles = new[] { new ArrTitleFolder(1, "/movies/Movies") };

        Assert.Null(ArrRescanTarget.TitleFor("/media/Movies/Ronin/Ronin.mkv", titles, ArrRescanTarget.MovieFolderDepth));
        Assert.Null(ArrRescanTarget.TitleFor("/media/TV/Show/Season 01/Show - S01E01.mkv", [new ArrTitleFolder(2, "/tv/TV")], ArrRescanTarget.SeriesFolderDepth));
    }

    [Fact]
    public void An_episode_directly_in_its_series_folder_is_found()
    {
        var titles = new[] { new ArrTitleFolder(3, "/tv/Taxi") };

        Assert.Equal(3, ArrRescanTarget.TitleFor("/data/tv/Taxi/Taxi - S01E01.mkv", titles, ArrRescanTarget.SeriesFolderDepth));
    }

    [Fact]
    public void Reads_ids_and_paths_from_a_movie_or_series_list()
    {
        var titles = ArrTrackedFileParser.ParseTitleFolders("""[{"id":1,"path":"/movies/Heat (1995)"},{"id":2,"path":"/movies/Ronin"}]""");

        Assert.Equal([new ArrTitleFolder(1, "/movies/Heat (1995)"), new ArrTitleFolder(2, "/movies/Ronin")], titles);
    }

    [Theory]
    [InlineData("""{"id":1,"path":"/a"}""")]
    [InlineData("""[{"path":"/a"}]""")]
    [InlineData("""[{"id":1}]""")]
    [InlineData("not json")]
    public void A_list_that_is_not_what_a_manager_sends_reads_as_nothing(string json)
    {
        Assert.Null(ArrTrackedFileParser.ParseTitleFolders(json));
    }
}
