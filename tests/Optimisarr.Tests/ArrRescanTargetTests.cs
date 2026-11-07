using Optimisarr.Core.Activity;

namespace Optimisarr.Tests;

public sealed class ArrRescanTargetTests
{
    [Fact]
    public void Finds_the_movie_whose_folder_holds_the_file_even_under_a_different_mount()
    {
        var titles = new[] { new ArrTitleFolder(7, "/movies/Heat (1995)"), new ArrTitleFolder(8, "/movies/Ronin (1998)") };

        Assert.Equal(7, ArrRescanTarget.TitleFor("/media/films/Heat (1995)/Heat (1995).mp4", titles));
    }

    [Fact]
    public void Finds_the_series_from_an_episode_inside_a_season_folder()
    {
        var titles = new[] { new ArrTitleFolder(3, "/tv/The Wire"), new ArrTitleFolder(4, "/tv/Taxi") };

        Assert.Equal(3, ArrRescanTarget.TitleFor("/data/tv/The Wire/Season 01/The Wire - S01E01.mkv", titles));
    }

    [Fact]
    public void Folder_names_compare_like_the_tracked_file_matcher_does()
    {
        var titles = new[] { new ArrTitleFolder(5, "D:\\Movies\\Amélie (2001)") };

        Assert.Equal(5, ArrRescanTarget.TitleFor("/media/movies/AME\u0301LIE (2001)/Amélie.mkv", titles));
    }

    [Fact]
    public void Two_titles_with_the_same_folder_name_are_told_apart_by_their_parent_folders()
    {
        var titles = new[] { new ArrTitleFolder(1, "/movies/hd/Heat (1995)"), new ArrTitleFolder(2, "/movies/uhd/Heat (1995)") };

        Assert.Equal(2, ArrRescanTarget.TitleFor("/media/uhd/Heat (1995)/Heat.mkv", titles));
    }

    [Fact]
    public void Two_titles_that_cannot_be_told_apart_give_no_answer()
    {
        var titles = new[] { new ArrTitleFolder(1, "/a/Heat (1995)"), new ArrTitleFolder(2, "/b/Heat (1995)") };

        Assert.Null(ArrRescanTarget.TitleFor("/media/c/Heat (1995)/Heat.mkv", titles));
    }

    [Fact]
    public void A_file_outside_every_title_folder_gives_no_answer()
    {
        var titles = new[] { new ArrTitleFolder(1, "/movies/Heat (1995)") };

        Assert.Null(ArrRescanTarget.TitleFor("/media/movies/Ronin (1998)/Ronin.mkv", titles));
    }

    [Fact]
    public void The_file_name_itself_never_matches_a_title_folder()
    {
        var titles = new[] { new ArrTitleFolder(1, "/movies/Heat.mkv") };

        Assert.Null(ArrRescanTarget.TitleFor("/media/loose/Heat.mkv", titles));
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
