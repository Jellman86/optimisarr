using Optimisarr.Core.Activity;

namespace Optimisarr.Tests;

public sealed class ConnectionDiscoveryParserTests
{
    private const string ResourcesJson = """
    [
      {
        "name": "Living Room",
        "provides": "server",
        "clientIdentifier": "abc123",
        "accessToken": "tok-server-1",
        "connections": [
          { "protocol": "https", "address": "1.2.3.4", "port": 32400, "uri": "https://remote.plex.direct:32400", "local": false, "relay": false },
          { "protocol": "https", "address": "192.168.1.10", "port": 32400, "uri": "https://local.plex.direct:32400", "local": true, "relay": false },
          { "protocol": "https", "address": "1.2.3.4", "port": 443, "uri": "https://relay.plex.direct:443", "local": false, "relay": true }
        ]
      },
      {
        "name": "My Phone",
        "provides": "client,player",
        "connections": [ { "protocol": "https", "uri": "https://phone", "local": true, "relay": false } ]
      }
    ]
    """;

    [Fact]
    public void Plex_resources_returns_only_servers_with_their_token()
    {
        var servers = PlexResourcesParser.ParseServers(ResourcesJson);

        Assert.Single(servers); // the client/player resource is excluded
        Assert.Equal("Living Room", servers[0].Name);
        Assert.Equal("tok-server-1", servers[0].AccessToken);
        Assert.Equal(3, servers[0].Connections.Count);
    }

    [Fact]
    public void Plex_best_connection_prefers_local_non_relay()
    {
        var server = PlexResourcesParser.ParseServers(ResourcesJson)[0];

        var best = server.BestConnection();

        Assert.NotNull(best);
        Assert.Equal("https://local.plex.direct:32400", best!.Uri);
        Assert.True(best.Local);
    }

    [Fact]
    public void Plex_best_connection_falls_back_to_non_relay_then_relay()
    {
        // No local connection: should pick the remote non-relay over the relay.
        var json = """
        [{ "name": "S", "provides": "server", "connections": [
          { "protocol": "https", "uri": "https://relay", "local": false, "relay": true },
          { "protocol": "https", "uri": "https://remote", "local": false, "relay": false }
        ]}]
        """;

        var best = PlexResourcesParser.ParseServers(json)[0].BestConnection();

        Assert.Equal("https://remote", best!.Uri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void Plex_resources_is_empty_for_bad_or_unexpected_payloads(string json)
    {
        Assert.Empty(PlexResourcesParser.ParseServers(json));
    }

    [Fact]
    public void Plex_info_reads_friendly_name_and_version()
    {
        var info = MediaServerInfoParser.ParsePlex("""{ "MediaContainer": { "friendlyName": "Tower", "version": "1.40.1" } }""");

        Assert.NotNull(info);
        Assert.Equal("Tower", info!.Name);
        Assert.Equal("1.40.1", info.Version);
    }

    [Fact]
    public void Jellyfin_info_reads_server_name_and_version()
    {
        var info = MediaServerInfoParser.ParseJellyfin("""{ "ServerName": "Den", "Version": "10.9.6", "Id": "x" }""");

        Assert.NotNull(info);
        Assert.Equal("Den", info!.Name);
        Assert.Equal("10.9.6", info.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("{\"unexpected\":true}")]
    public void Server_info_is_null_for_bad_payloads(string json)
    {
        Assert.Null(MediaServerInfoParser.ParsePlex(json));
        Assert.Null(MediaServerInfoParser.ParseJellyfin(json));
    }
}
