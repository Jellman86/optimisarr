using Optimisarr.Core.Activity;
using Optimisarr.Core.Domain;

namespace Optimisarr.Tests;

/// <summary>
/// Payloads are fabricated in the shape Sonarr and Radarr's webhook connection sends (Newtonsoft,
/// camelCase properties, the event type as its PascalCase name, null fields left out), as read from
/// src/NzbDrone.Core/Notifications/Webhook/ on both projects' develop branches.
/// </summary>
public sealed class ArrWebhookPayloadParserTests
{
    [Fact]
    public void A_sonarr_import_names_the_episode_file()
    {
        const string json = """
            {
              "series": { "id": 7, "title": "Fabricated Show", "path": "/tv/Fabricated Show" },
              "episodes": [ { "id": 70, "episodeNumber": 1, "seasonNumber": 1, "title": "Pilot" } ],
              "episodeFile": {
                "id": 700,
                "relativePath": "Season 01/Fabricated Show - S01E01.mkv",
                "path": "/tv/Fabricated Show/Season 01/Fabricated Show - S01E01.mkv",
                "size": 123456
              },
              "isUpgrade": false,
              "eventType": "Download",
              "instanceName": "Sonarr"
            }
            """;

        var parsed = ArrWebhookPayloadParser.Parse(json, ArrConnectionType.Sonarr);

        var import = Assert.IsType<ArrWebhookEvent.Import>(parsed);
        Assert.Equal(["/tv/Fabricated Show/Season 01/Fabricated Show - S01E01.mkv"], import.Paths);
    }

    [Fact]
    public void A_sonarr_upgrade_is_an_import_of_the_new_file()
    {
        const string json = """
            {
              "eventType": "Download",
              "series": { "path": "/tv/Show" },
              "episodeFile": { "relativePath": "S01E01.mkv", "path": "/tv/Show/S01E01.mkv" },
              "isUpgrade": true,
              "deletedFiles": [ { "path": "/tv/Show/S01E01.avi", "recycleBinPath": "/recycle/S01E01.avi" } ]
            }
            """;

        var import = Assert.IsType<ArrWebhookEvent.Import>(ArrWebhookPayloadParser.Parse(json, ArrConnectionType.Sonarr));

        Assert.Equal(["/tv/Show/S01E01.mkv"], import.Paths);
    }

    [Fact]
    public void A_sonarr_import_complete_names_every_episode_file()
    {
        const string json = """
            {
              "eventType": "Download",
              "series": { "path": "/tv/Show" },
              "episodeFiles": [
                { "relativePath": "Season 01/S01E01.mkv", "path": "/tv/Show/Season 01/S01E01.mkv" },
                { "relativePath": "Season 01/S01E02.mkv" }
              ],
              "fileCount": 2
            }
            """;

        var import = Assert.IsType<ArrWebhookEvent.Import>(ArrWebhookPayloadParser.Parse(json, ArrConnectionType.Sonarr));

        Assert.Equal(["/tv/Show/Season 01/S01E01.mkv", "/tv/Show/Season 01/S01E02.mkv"], import.Paths);
    }

    [Fact]
    public void A_radarr_import_names_the_movie_file()
    {
        const string json = """
            {
              "movie": { "id": 3, "title": "Fabricated Film", "year": 2021, "folderPath": "/movies/Fabricated Film (2021)" },
              "remoteMovie": { "tmdbId": 1, "title": "Fabricated Film", "year": 2021 },
              "movieFile": {
                "id": 30,
                "relativePath": "Fabricated Film (2021).mkv",
                "path": "/movies/Fabricated Film (2021)/Fabricated Film (2021).mkv",
                "size": 999
              },
              "isUpgrade": true,
              "eventType": "Download"
            }
            """;

        var import = Assert.IsType<ArrWebhookEvent.Import>(ArrWebhookPayloadParser.Parse(json, ArrConnectionType.Radarr));

        Assert.Equal(["/movies/Fabricated Film (2021)/Fabricated Film (2021).mkv"], import.Paths);
    }

    [Fact]
    public void A_radarr_file_without_a_path_is_found_from_its_folder_and_relative_path()
    {
        const string json = """
            {
              "eventType": "Download",
              "movie": { "folderPath": "C:\\Movies\\Film (2020)" },
              "movieFile": { "relativePath": "Film (2020).mkv" }
            }
            """;

        var import = Assert.IsType<ArrWebhookEvent.Import>(ArrWebhookPayloadParser.Parse(json, ArrConnectionType.Radarr));

        Assert.Equal([@"C:\Movies\Film (2020)\Film (2020).mkv"], import.Paths);
    }

    [Theory]
    [InlineData(ArrConnectionType.Sonarr, """{ "eventType": "Test", "series": { "id": 1, "title": "Test Title", "path": "C:\\testpath" } }""")]
    [InlineData(ArrConnectionType.Radarr, """{ "eventType": "Test", "movie": { "id": 1, "title": "Test Title", "folderPath": "C:\\testpath" } }""")]
    public void The_managers_test_event_is_a_test(ArrConnectionType type, string json)
    {
        Assert.IsType<ArrWebhookEvent.Test>(ArrWebhookPayloadParser.Parse(json, type));
    }

    [Theory]
    [InlineData("Grab")]
    [InlineData("Rename")]
    [InlineData("SeriesDelete")]
    [InlineData("EpisodeFileDelete")]
    [InlineData("Health")]
    [InlineData("ApplicationUpdate")]
    [InlineData("SomethingNew")]
    public void Every_other_event_is_ignored_by_name(string eventType)
    {
        var parsed = ArrWebhookPayloadParser.Parse($$"""{ "eventType": "{{eventType}}", "series": { "path": "/tv/Show" } }""", ArrConnectionType.Sonarr);

        Assert.Equal(eventType, Assert.IsType<ArrWebhookEvent.Ignored>(parsed).EventType);
    }

    [Fact]
    public void A_radarr_payload_sent_to_a_sonarr_connection_names_no_file()
    {
        const string json = """{ "eventType": "Download", "movieFile": { "path": "/movies/Film/Film.mkv" } }""";

        Assert.IsType<ArrWebhookEvent.Invalid>(ArrWebhookPayloadParser.Parse(json, ArrConnectionType.Sonarr));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "series": {} }""")]
    [InlineData("""{ "eventType": 4 }""")]
    [InlineData("""{ "eventType": "Download", "episodeFile": "nope" }""")]
    public void Malformed_payloads_are_invalid(string json)
    {
        Assert.IsType<ArrWebhookEvent.Invalid>(ArrWebhookPayloadParser.Parse(json, ArrConnectionType.Sonarr));
    }

    [Fact]
    public void An_import_naming_too_many_files_is_invalid()
    {
        var files = string.Join(",", Enumerable.Range(0, ArrWebhookPayloadParser.MaxFilesPerEvent + 1)
            .Select(index => $$"""{ "path": "/tv/Show/E{{index}}.mkv" }"""));

        var parsed = ArrWebhookPayloadParser.Parse($$"""{ "eventType": "Download", "episodeFiles": [{{files}}] }""", ArrConnectionType.Sonarr);

        Assert.IsType<ArrWebhookEvent.Invalid>(parsed);
    }
}
