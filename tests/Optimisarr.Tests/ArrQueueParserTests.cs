using Optimisarr.Core.Activity;

namespace Optimisarr.Tests;

public sealed class ArrQueueParserTests
{
    [Fact]
    public void Reads_embedded_series_paths_from_a_sonarr_queue()
    {
        const string json = """
        {
          "records": [
            { "seriesId": 1, "status": "downloading", "series": { "path": "/data/tv/Show A" } },
            { "seriesId": 2, "status": "queued", "series": { "path": "/data/tv/Show B" } }
          ]
        }
        """;

        var folders = ArrQueueParser.ParseActiveFolders(json);

        Assert.Equal(["/data/tv/Show A", "/data/tv/Show B"], folders);
    }

    [Fact]
    public void Reads_embedded_movie_paths_from_a_radarr_queue()
    {
        const string json = """
        { "records": [ { "movieId": 7, "movie": { "path": "/data/films/Heat (1995)" } } ] }
        """;

        var folders = ArrQueueParser.ParseActiveFolders(json);

        Assert.Equal("/data/films/Heat (1995)", Assert.Single(folders));
    }

    [Fact]
    public void Deduplicates_repeated_folders()
    {
        const string json = """
        {
          "records": [
            { "series": { "path": "/data/tv/Show A" } },
            { "series": { "path": "/data/tv/Show A" } }
          ]
        }
        """;

        Assert.Single(ArrQueueParser.ParseActiveFolders(json));
    }

    [Fact]
    public void Ignores_records_without_an_embedded_title_path()
    {
        const string json = """
        { "records": [ { "seriesId": 1, "status": "downloading" } ] }
        """;

        Assert.Empty(ArrQueueParser.ParseActiveFolders(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{ \"records\": {} }")]
    public void Malformed_or_empty_responses_yield_no_folders(string json)
    {
        Assert.Empty(ArrQueueParser.ParseActiveFolders(json));
    }
}
