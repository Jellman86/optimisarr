using Optimisarr.Core.Activity;

namespace Optimisarr.Tests;

public sealed class ArrTrackedFilesTests
{
    private static ArrInventoryFile File(int id, string path) => new(id, path);

    [Fact]
    public void A_tracked_file_is_found_though_the_manager_mounts_the_library_elsewhere()
    {
        var match = ArrTrackedFileMatcher.Match(
            ["/movies/Film (2020)/Film (2020).mkv"],
            [File(1, "/data/Film/Film (2020)/Film (2020).mkv"), File(2, "/data/Film/Other (2021)/Other (2021).mkv")]);

        Assert.Equal([1], match.TrackedMediaFileIds);
        Assert.Equal(new ArrPathRoot("/movies", "/data/Film"), Assert.Single(match.Roots));
        Assert.Equal(0, match.Unmatched);
    }

    [Fact]
    public void A_spare_copy_elsewhere_is_not_tracked_when_the_mount_is_known_from_another_file()
    {
        var match = ArrTrackedFileMatcher.Match(
            ["/movies/Known (2019)/Known (2019).mkv", "/movies/Film (2020)/Film (2020).mkv"],
            [
                File(1, "/data/Film/Known (2019)/Known (2019).mkv"),
                File(2, "/data/Film/Film (2020)/Film (2020).mkv"),
                File(3, "/data/Backup/Film (2020)/Film (2020).mkv"),
            ]);

        Assert.Equal([1, 2], match.TrackedMediaFileIds.Order());
    }

    [Fact]
    public void Two_copies_it_cannot_tell_apart_both_count_as_tracked_so_neither_is_moved()
    {
        var match = ArrTrackedFileMatcher.Match(
            ["/movies/Film (2020)/Film (2020).mkv"],
            [File(1, "/data/A/Film (2020)/Film (2020).mkv"), File(2, "/data/B/Film (2020)/Film (2020).mkv")]);

        Assert.Equal([1, 2], match.TrackedMediaFileIds.Order());
        Assert.Equal(1, match.Ambiguous);
    }

    [Fact]
    public void A_file_whose_extension_changed_before_the_manager_rescanned_still_counts_as_tracked()
    {
        var match = ArrTrackedFileMatcher.Match(
            ["/tv/Show/Season 1/Show S01E01.mkv"],
            [File(1, "/data/TV/Show/Season 1/Show S01E01.mp4")]);

        Assert.Equal([1], match.TrackedMediaFileIds);
        Assert.Equal(new ArrPathRoot("/tv", "/data/TV"), Assert.Single(match.Roots));
    }

    [Fact]
    public void The_same_name_in_a_different_folder_counts_as_tracked_rather_than_risk_moving_it()
    {
        var match = ArrTrackedFileMatcher.Match(["/movies/Film.mkv"], [File(1, "/data/Film.mkv")]);

        Assert.Equal([1], match.TrackedMediaFileIds);
    }

    [Fact]
    public void A_manager_on_windows_matches_a_library_on_linux()
    {
        var match = ArrTrackedFileMatcher.Match(
            [@"D:\Movies\Film (2020)\Film (2020).mkv"],
            [File(1, "/data/Film/Film (2020)/Film (2020).mkv")]);

        Assert.Equal([1], match.TrackedMediaFileIds);
    }

    [Fact]
    public void A_tracked_file_outside_the_libraries_changes_nothing()
    {
        var match = ArrTrackedFileMatcher.Match(["/4k/Other/Other.mkv"], [File(1, "/data/Film/Film/Film.mkv")]);

        Assert.Empty(match.TrackedMediaFileIds);
        Assert.Equal(1, match.Unmatched);
    }

    [Fact]
    public void Radarr_movies_give_the_paths_of_those_that_have_a_file()
    {
        var paths = ArrTrackedFileParser.ParseMovieFilePaths(
            """[{ "title": "A", "movieFile": { "path": "/movies/A/A.mkv" } }, { "title": "B" }, { "title": "C", "movieFile": null }]""");

        Assert.Equal(["/movies/A/A.mkv"], paths);
    }

    [Fact]
    public void Sonarr_series_ids_and_episode_files_are_read()
    {
        Assert.Equal([3, 9], ArrTrackedFileParser.ParseSeriesIds("""[{ "id": 3 }, { "id": 9 }]"""));
        Assert.Equal(["/tv/S/e.mkv"], ArrTrackedFileParser.ParseEpisodeFilePaths("""[{ "path": "/tv/S/e.mkv" }, { "relativePath": "x" }]"""));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{ \"message\": \"Unauthorized\" }")]
    [InlineData("")]
    public void An_unreadable_answer_is_null_so_it_can_never_read_as_tracks_nothing(string json)
    {
        Assert.Null(ArrTrackedFileParser.ParseMovieFilePaths(json));
        Assert.Null(ArrTrackedFileParser.ParseSeriesIds(json));
        Assert.Null(ArrTrackedFileParser.ParseEpisodeFilePaths(json));
    }
}
