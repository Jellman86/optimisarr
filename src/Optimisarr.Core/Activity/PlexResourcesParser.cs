using System.Text.Json;

namespace Optimisarr.Core.Activity;

/// <summary>One reachable address for a Plex server from the plex.tv resources list.</summary>
public sealed record PlexConnection(string Uri, bool Local, bool Relay, string Protocol);

/// <summary>
/// A Plex server discovered on the signed-in account (plex.tv <c>/api/v2/resources</c>), with its
/// own access token and the addresses it can be reached at. Discovery means the user never has to
/// find a host/port or a raw token — they pick a server and we fill the connection.
/// </summary>
public sealed record PlexServerResource(
    string Name,
    string? AccessToken,
    string? ClientIdentifier,
    IReadOnlyList<PlexConnection> Connections)
{
    /// <summary>
    /// The address to prefer: a local, non-relay connection first, then any non-relay, then
    /// anything; HTTPS beats HTTP within each tier (the <c>*.plex.direct</c> URIs carry valid certs).
    /// </summary>
    public PlexConnection? BestConnection() =>
        Connections
            .OrderByDescending(c => c.Local && !c.Relay)
            .ThenByDescending(c => !c.Relay)
            .ThenByDescending(c => c.Protocol.Equals("https", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
}

/// <summary>
/// Parses the plex.tv <c>/api/v2/resources</c> JSON into the owned/shared servers on the account.
/// Pure (string in, records out) so it is trivially unit tested; the API layer makes the HTTP call.
/// </summary>
public static class PlexResourcesParser
{
    public static IReadOnlyList<PlexServerResource> ParseServers(string? json)
    {
        var servers = new List<PlexServerResource>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return servers;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return servers;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return servers;
            }

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                // `provides` is a CSV of roles, e.g. "server" or "client,player"; keep only servers.
                var provides = GetString(element, "provides") ?? "";
                var isServer = provides
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(role => role.Equals("server", StringComparison.OrdinalIgnoreCase));
                if (!isServer)
                {
                    continue;
                }

                var connections = new List<PlexConnection>();
                if (element.TryGetProperty("connections", out var conns) && conns.ValueKind == JsonValueKind.Array)
                {
                    foreach (var connection in conns.EnumerateArray())
                    {
                        var uri = GetString(connection, "uri");
                        if (string.IsNullOrWhiteSpace(uri))
                        {
                            continue;
                        }

                        connections.Add(new PlexConnection(
                            uri,
                            GetBool(connection, "local"),
                            GetBool(connection, "relay"),
                            GetString(connection, "protocol") ?? "https"));
                    }
                }

                if (connections.Count == 0)
                {
                    continue;
                }

                servers.Add(new PlexServerResource(
                    GetString(element, "name") ?? "Plex Server",
                    GetString(element, "accessToken"),
                    GetString(element, "clientIdentifier"),
                    connections));
            }
        }

        return servers;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        && value.GetBoolean();
}
