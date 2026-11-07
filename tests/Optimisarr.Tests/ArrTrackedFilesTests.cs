using Optimisarr.Core.Activity;

namespace Optimisarr.Tests;

public sealed class ArrTrackedFilesTests
{
    private static ArrInventoryFile File(int id, string path) => new(id, path);

    private static readonly string[] Known = ["/movies/K1/K1.mkv", "/movies/K2/K2.mkv"];
    private static readonly ArrInventoryFile[] KnownFiles = [File(101, "/data/Film/K1/K1.mkv"), File(102, "/data/Film/K2/K2.mkv")];

    [Fact]
    public void A_tracked_file_is_found_though_the_manager_mounts_the_library_elsewhere()
    {
        var match = ArrTrackedFileMatcher.Match(
            ["/movies/Film (2020)/Film (2020).mkv"],
            [File(1, "/data/Film/Film (2020)/Film (2020).mkv"), File(2, "/data/Film/Other (2021)/Other (2021).mkv")]);

        Assert.Equal([1], match.TrackedMediaFileIds);
        Assert.Empty(match.PossiblyTrackedMediaFileIds);
        Assert.Equal(new ArrPathRoot("/MOVIES", "/DATA/FILM"), Assert.Single(match.Roots));
        Assert.Equal(0, match.NotFound);
    }

    [Fact]
    public void A_mount_most_files_agree_on_settles_a_tie_and_the_other_copy_stays_possible()
    {
        var match = ArrTrackedFileMatcher.Match(
            [.. Known, "/movies/Film (2020)/Film (2020).mkv"],
            [.. KnownFiles, File(1, "/data/Film/Film (2020)/Film (2020).mkv"), File(2, "/data/Backup/Film (2020)/Film (2020).mkv")]);

        Assert.Contains(1, match.TrackedMediaFileIds);
        Assert.Equal([2], match.PossiblyTrackedMediaFileIds);
        Assert.Equal(0, match.Ambiguous);
    }

    [Fact]
    public void One_stray_match_to_a_backup_cannot_settle_a_tie()
    {
        var match = ArrTrackedFileMatcher.Match(
            ["/movies/Only/Only.mkv", "/movies/Film/Film.mkv"],
            [File(1, "/backup/Only/Only.mkv"), File(2, "/data/Film/Film.mkv"), File(3, "/backup/Film/Film.mkv")]);

        Assert.DoesNotContain(3, match.TrackedMediaFileIds);
        Assert.DoesNotContain(2, match.TrackedMediaFileIds);
        Assert.Equal([2, 3], match.PossiblyTrackedMediaFileIds.Order());
        Assert.Equal(1, match.Ambiguous);
    }

    [Fact]
    public void Two_copies_it_cannot_tell_apart_are_both_possible_and_neither_is_called_a_spare()
    {
        var match = ArrTrackedFileMatcher.Match(
            ["/movies/Film (2020)/Film (2020).mkv"],
            [File(1, "/data/A/Film (2020)/Film (2020).mkv"), File(2, "/data/B/Film (2020)/Film (2020).mkv")]);

        Assert.Empty(match.TrackedMediaFileIds);
        Assert.Equal([1, 2], match.PossiblyTrackedMediaFileIds.Order());
        Assert.Equal(1, match.Ambiguous);
    }

    [Fact]
    public void The_real_file_behind_a_link_stays_possible_though_a_spare_looks_closer()
    {
        var match = ArrTrackedFileMatcher.Match(
            ["/movies/Film/Film.mkv"],
            [File(1, "/data/current/Film.mkv"), File(2, "/backup/Film/Film.mkv")]);

        Assert.Equal([2], match.TrackedMediaFileIds);
        Assert.Equal([1], match.PossiblyTrackedMediaFileIds);
    }

    [Fact]
    public void A_file_whose_extension_changed_before_the_manager_rescanned_still_counts_as_tracked()
    {
        var match = ArrTrackedFileMatcher.Match(
            ["/tv/Show/Season 1/Show S01E01.mkv"],
            [File(1, "/data/TV/Show/Season 1/Show S01E01.mp4")]);

        Assert.Equal([1], match.TrackedMediaFileIds);
        Assert.Equal(new ArrPathRoot("/", "/DATA"), Assert.Single(match.Roots));
    }

    [Fact]
    public void The_same_name_in_an_unrelated_folder_is_only_possible()
    {
        var match = ArrTrackedFileMatcher.Match(["/movies/Film.mkv"], [File(1, "/data/Film.mkv")]);

        Assert.Empty(match.TrackedMediaFileIds);
        Assert.Equal([1], match.PossiblyTrackedMediaFileIds);
    }

    [Theory]
    [InlineData("/movies/Film/Film.mkv", "/data/film/film.mkv")]
    [InlineData("/movies/Caf\u00e9/Caf\u00e9.mkv", "/data/Cafe\u0301/Cafe\u0301.mkv")]
    [InlineData("/movies/Film./Film.mkv", "/data/Film/Film.mkv")]
    [InlineData("/movies/Film/Film.mkv.", "/data/Film/Film.mkv")]
    [InlineData("/movies/Film/Film.mkv", "/data/Film/Film.mkv.")]
    [InlineData(@"D:\Movies\Film (2020)\Film (2020).mkv", "/data/Film (2020)/Film (2020).mkv")]
    [InlineData("/movies/Film.2020.1080p/Film.2020.1080p.mkv", "/data/Film.2020.1080p/Film.2020.1080p.mp4")]
    public void Names_are_compared_as_a_filesystem_or_another_operating_system_would_see_them(string manager, string local) =>
        Assert.Equal([1], ArrTrackedFileMatcher.Match([manager], [File(1, local)]).TrackedMediaFileIds);

    [Fact]
    public void A_tracked_file_outside_the_libraries_is_counted_as_not_found()
    {
        var match = ArrTrackedFileMatcher.Match(["/4k/Other/Other.mkv"], [File(1, "/data/Film/Film/Film.mkv")]);

        Assert.Empty(match.TrackedMediaFileIds);
        Assert.Empty(match.PossiblyTrackedMediaFileIds);
        Assert.Equal(1, match.NotFound);
    }

    [Fact]
    public void A_library_full_of_files_with_one_name_is_matched_quickly()
    {
        const int Files = 100_000;
        var inventory = Enumerable.Range(1, Files).Select(id => File(id, $"/data/Title {id}/video.mkv")).ToList();
        var paths = Enumerable.Range(1, Files).Select(id => $"/movies/Title {id}/video.mkv").ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var match = ArrTrackedFileMatcher.Match(paths, inventory);

        Assert.Equal(Files, match.TrackedMediaFileIds.Count);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
    }

    [Fact]
    public void Matching_stops_when_cancelled()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => ArrTrackedFileMatcher.Match(["/a/b.mkv"], [File(1, "/a/b.mkv")], cancelled.Token));
    }

    [Fact]
    public void Radarr_movies_give_the_paths_of_those_that_have_a_file()
    {
        var paths = ArrTrackedFileParser.ParseMovieFilePaths(
            """[{ "id": 1, "hasFile": true, "movieFile": { "path": "/movies/A/A.mkv" } }, { "id": 2, "hasFile": false }, { "id": 3, "hasFile": false, "movieFile": null }]""");

        Assert.Equal(["/movies/A/A.mkv"], paths);
    }

    [Fact]
    public void Sonarr_series_ids_and_episode_files_are_read()
    {
        Assert.Equal([3, 9], ArrTrackedFileParser.ParseSeriesIds("""[{ "id": 3 }, { "id": 9 }]"""));
        Assert.Equal(["/tv/S/e.mkv"], ArrTrackedFileParser.ParseEpisodeFilePaths("""[{ "id": 1, "path": "/tv/S/e.mkv" }]"""));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{ \"message\": \"Unauthorized\" }")]
    [InlineData("")]
    [InlineData("[{}]")]
    [InlineData("[42]")]
    public void An_answer_that_is_not_what_a_manager_sends_is_null_so_it_can_never_read_as_tracks_nothing(string json)
    {
        Assert.Null(ArrTrackedFileParser.ParseMovieFilePaths(json));
        Assert.Null(ArrTrackedFileParser.ParseSeriesIds(json));
        Assert.Null(ArrTrackedFileParser.ParseEpisodeFilePaths(json));
    }

    [Theory]
    [InlineData("""[{ "id": 1, "hasFile": true }]""")]
    [InlineData("""[{ "id": 1, "hasFile": true, "movieFile": { "relativePath": "Film.mkv" } }]""")]
    [InlineData("""[{ "id": 1, "hasFile": true, "movieFile": { "path": "" } }]""")]
    [InlineData("""[{ "id": 1 }]""")]
    [InlineData("""[{ "id": 1, "label": "not a movie" }]""")]
    [InlineData("""[{ "id": 1, "hasFile": "true" }]""")]
    public void A_movie_that_claims_a_file_without_a_path_makes_the_answer_unusable(string json) =>
        Assert.Null(ArrTrackedFileParser.ParseMovieFilePaths(json));

    [Theory]
    [InlineData("""{ "appName": "Radarr", "version": "5.0" }""", "Radarr", true)]
    [InlineData("""{ "appName": "Sonarr", "version": "4.0" }""", "sonarr", true)]
    [InlineData("""{ "appName": "Sonarr", "version": "4.0" }""", "Radarr", false)]
    [InlineData("""{ "appName": "Radarr" }""", "Radarr", false)]
    [InlineData("""[]""", "Radarr", false)]
    [InlineData("""<html></html>""", "Radarr", false)]
    public void Only_the_expected_manager_is_trusted(string json, string app, bool expected) =>
        Assert.Equal(expected, ArrTrackedFileParser.IsManager(json, app));
}
