using Optimisarr.Core.Activity;

namespace Optimisarr.Tests;

public sealed class ArrWebhookPathResolverTests
{
    private static readonly IReadOnlyList<WebhookLibraryRoot> Libraries =
    [
        new(1, "/data/tv"),
        new(2, "/data/tv/anime"),
        new(3, "/data/movies/"),
    ];

    [Fact]
    public void A_path_the_manager_shares_with_optimisarr_resolves_to_its_library()
    {
        var resolved = ArrWebhookPathResolver.Resolve("/data/movies/Film (2020)/Film.mkv", null, Libraries);

        var found = Assert.IsType<WebhookPathResolution.InLibrary>(resolved);
        Assert.Equal(3, found.LibraryId);
        Assert.Equal("/data/movies/Film (2020)/Film.mkv", found.LocalPath);
    }

    [Fact]
    public void The_longest_matching_library_root_wins()
    {
        var resolved = ArrWebhookPathResolver.Resolve("/data/tv/anime/Show/S01E01.mkv", null, Libraries);

        Assert.Equal(2, Assert.IsType<WebhookPathResolution.InLibrary>(resolved).LibraryId);
    }

    [Theory]
    [InlineData("/data/tvshows/Show/S01E01.mkv")]
    [InlineData("/elsewhere/Show/S01E01.mkv")]
    [InlineData("/data/tv")]
    public void A_path_outside_every_library_root_is_not_in_a_library(string path)
    {
        Assert.IsType<WebhookPathResolution.NotInLibrary>(ArrWebhookPathResolver.Resolve(path, null, Libraries));
    }

    [Theory]
    [InlineData("/data/tv/../../etc/passwd")]
    [InlineData("/data/tv/Show/../../../etc/shadow.mkv")]
    [InlineData("/data/tv/Show/..")]
    [InlineData("relative/Show/S01E01.mkv")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/data/tv/Show/S01\0E01.mkv")]
    public void A_path_that_could_escape_a_library_is_refused(string path)
    {
        Assert.IsType<WebhookPathResolution.Refused>(ArrWebhookPathResolver.Resolve(path, null, Libraries));
    }

    [Fact]
    public void A_mapping_translates_the_managers_mount_to_optimisarrs()
    {
        var mapping = new ArrPathMapping("/tv", "/data/tv");

        var resolved = ArrWebhookPathResolver.Resolve("/tv/Show/Season 01/S01E01.mkv", mapping, Libraries);

        var found = Assert.IsType<WebhookPathResolution.InLibrary>(resolved);
        Assert.Equal(1, found.LibraryId);
        Assert.Equal("/data/tv/Show/Season 01/S01E01.mkv", found.LocalPath);
    }

    [Fact]
    public void A_mapping_matches_whole_folders_not_string_prefixes()
    {
        var mapping = new ArrPathMapping("/tv", "/data/tv");

        Assert.IsType<WebhookPathResolution.NotInLibrary>(
            ArrWebhookPathResolver.Resolve("/tvshows/Show/S01E01.mkv", mapping, Libraries));
    }

    [Fact]
    public void A_windows_manager_path_maps_onto_a_linux_library()
    {
        var mapping = new ArrPathMapping(@"D:\Media\TV\", "/data/tv");

        var resolved = ArrWebhookPathResolver.Resolve(@"d:\media\tv\Show\Season 01\S01E01.mkv", mapping, Libraries);

        var found = Assert.IsType<WebhookPathResolution.InLibrary>(resolved);
        Assert.Equal("/data/tv/Show/Season 01/S01E01.mkv", found.LocalPath);
    }

    [Fact]
    public void A_mapping_cannot_be_used_to_climb_out_of_a_library()
    {
        var mapping = new ArrPathMapping("/tv", "/data/tv");

        Assert.IsType<WebhookPathResolution.Refused>(
            ArrWebhookPathResolver.Resolve("/tv/../movies/Film/Film.mkv", mapping, Libraries));
    }

    [Fact]
    public void A_path_outside_the_mapping_is_still_matched_as_given()
    {
        var mapping = new ArrPathMapping("/tv", "/data/tv");

        Assert.Equal(3, Assert.IsType<WebhookPathResolution.InLibrary>(
            ArrWebhookPathResolver.Resolve("/data/movies/Film/Film.mkv", mapping, Libraries)).LibraryId);
    }

    [Theory]
    [InlineData("/data/tv/Show", "S01E01.mkv", "/data/tv/Show/S01E01.mkv")]
    [InlineData("/data/tv/Show/", "Season 01/S01E01.mkv", "/data/tv/Show/Season 01/S01E01.mkv")]
    [InlineData(@"D:\TV\Show", @"Season 01\S01E01.mkv", @"D:\TV\Show\Season 01\S01E01.mkv")]
    public void A_folder_and_relative_path_are_joined_in_the_managers_style(string folder, string relative, string expected)
    {
        Assert.Equal(expected, ArrWebhookPathResolver.Join(folder, relative));
    }
}
