using System.Text;
using Optimisarr.Api.Library;
using Optimisarr.Core.Activity;
using Optimisarr.Core.Domain;

namespace Optimisarr.Api.Replacement;

/// <summary>Where to send the user, and how to poll, for a Plex sign-in.</summary>
public sealed record PlexConnectStart(long Id, string Code, string AuthUrl);

/// <summary>The Quick Connect code to show the user and the secret to poll with.</summary>
public sealed record JellyfinConnectStart(string Code, string Secret);

/// <summary>The result of polling a connect flow: authorised yet, and the token once it is.</summary>
public sealed record ConnectResult(bool Authorized, string? Token);

/// <summary>The outcome of a "Test connection": whether it worked, and the server it reached.</summary>
public sealed record ConnectionTestResult(bool Ok, string? ServerName, string? Version, string? Error);

/// <summary>A Plex server found on the account, ready to fill a connection in one click.</summary>
public sealed record PlexDiscoveredServer(string Name, string Uri, bool Local, string? AccessToken);

/// <summary>
/// Drives the interactive sign-in flows so a user never has to find and paste a raw
/// token: Plex's OAuth/PIN flow and Jellyfin's Quick Connect. Both are two-step —
/// start, then poll until the user approves — and the resulting token is handed back
/// to the client to save onto a watcher. Emby has no comparable flow and keeps its
/// manual API key.
/// </summary>
public sealed class ProviderConnectService(
    IHttpClientFactory httpClientFactory,
    SettingsStore settings)
{
    private const string Product = "Optimisarr";
    private const string PlexApi = "https://plex.tv/api/v2";

    public async Task<PlexConnectStart> StartPlexAsync(CancellationToken cancellationToken)
    {
        var clientId = await settings.GetOrCreatePlexClientIdentifierAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{PlexApi}/pins?strong=true");
        AddPlexHeaders(request, clientId);
        var json = await SendAsync(request, cancellationToken);

        var pin = PlexPinParser.ParsePin(json)
            ?? throw new InvalidOperationException("Plex did not return a sign-in PIN.");

        // The user opens this URL, signs in, and approves; the fragment carries our
        // client id and the PIN code so Plex ties the issued token to this install.
        var authUrl =
            $"https://app.plex.tv/auth#?clientID={Uri.EscapeDataString(clientId)}" +
            $"&code={Uri.EscapeDataString(pin.Code)}" +
            $"&context%5Bdevice%5D%5Bproduct%5D={Uri.EscapeDataString(Product)}";

        return new PlexConnectStart(pin.Id, pin.Code, authUrl);
    }

    public async Task<ConnectResult> PollPlexAsync(long pinId, CancellationToken cancellationToken)
    {
        var clientId = await settings.GetOrCreatePlexClientIdentifierAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{PlexApi}/pins/{pinId}");
        AddPlexHeaders(request, clientId);
        var json = await SendAsync(request, cancellationToken);

        var token = PlexPinParser.ParseAuthToken(json);
        return new ConnectResult(token is not null, token);
    }

    public async Task<JellyfinConnectStart> StartJellyfinAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var clientId = await settings.GetOrCreatePlexClientIdentifierAsync(cancellationToken);
        var root = baseUrl.TrimEnd('/');

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{root}/QuickConnect/Initiate");
        AddJellyfinHeaders(request, clientId, token: null);
        var json = await SendAsync(request, cancellationToken);

        var initiation = JellyfinQuickConnectParser.ParseInitiation(json)
            ?? throw new InvalidOperationException(
                "Quick Connect did not start. Make sure it is enabled in the Jellyfin server's dashboard.");

        return new JellyfinConnectStart(initiation.Code, initiation.Secret);
    }

    public async Task<ConnectResult> PollJellyfinAsync(string baseUrl, string secret, CancellationToken cancellationToken)
    {
        var clientId = await settings.GetOrCreatePlexClientIdentifierAsync(cancellationToken);
        var root = baseUrl.TrimEnd('/');

        using var connectRequest = new HttpRequestMessage(
            HttpMethod.Get, $"{root}/QuickConnect/Connect?secret={Uri.EscapeDataString(secret)}");
        AddJellyfinHeaders(connectRequest, clientId, token: null);
        var connectJson = await SendAsync(connectRequest, cancellationToken);

        if (!JellyfinQuickConnectParser.ParseAuthenticated(connectJson))
        {
            return new ConnectResult(false, null);
        }

        // Approved: exchange the secret for a durable access token.
        using var authRequest = new HttpRequestMessage(HttpMethod.Post, $"{root}/Users/AuthenticateWithQuickConnect");
        AddJellyfinHeaders(authRequest, clientId, token: null);
        authRequest.Content = new StringContent($"{{\"Secret\":\"{secret}\"}}", Encoding.UTF8, "application/json");
        var authJson = await SendAsync(authRequest, cancellationToken);

        var token = JellyfinQuickConnectParser.ParseAccessToken(authJson);
        return new ConnectResult(token is not null, token);
    }

    /// <summary>
    /// Lists the servers on the signed-in Plex account (from the OAuth token), each with its
    /// preferred address (local first) and its own access token, so the user can pick one instead
    /// of finding a host/port.
    /// </summary>
    public async Task<IReadOnlyList<PlexDiscoveredServer>> ListPlexServersAsync(string token, CancellationToken cancellationToken)
    {
        var clientId = await settings.GetOrCreatePlexClientIdentifierAsync(cancellationToken);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{PlexApi}/resources?includeHttps=1&includeRelay=1");
        AddPlexHeaders(request, clientId);
        request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
        var json = await SendAsync(request, cancellationToken);

        var discovered = new List<PlexDiscoveredServer>();
        foreach (var server in PlexResourcesParser.ParseServers(json))
        {
            var best = server.BestConnection();
            if (best is null)
            {
                continue;
            }

            // Use the server's own access token where present (correct for shared servers); fall
            // back to the account token for an owned server that omits it.
            discovered.Add(new PlexDiscoveredServer(server.Name, best.Uri, best.Local, server.AccessToken ?? token));
        }

        return discovered;
    }

    /// <summary>
    /// Tests a media-server connection: confirms the URL is reachable and the token is accepted,
    /// returning the server's name/version on success or a short reason on failure. Never throws —
    /// a failure is reported in the result so the UI can show it inline.
    /// </summary>
    public async Task<ConnectionTestResult> TestAsync(
        ActivityWatcherType type, string baseUrl, string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new ConnectionTestResult(false, null, null, "Enter the server's base URL first.");
        }
        if (string.IsNullOrWhiteSpace(token))
        {
            return new ConnectionTestResult(false, null, null, "No token to test — sign in or paste a token first.");
        }

        var root = baseUrl.TrimEnd('/');
        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            if (type == ActivityWatcherType.Plex)
            {
                var clientId = await settings.GetOrCreatePlexClientIdentifierAsync(cancellationToken);
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{root}/");
                AddPlexHeaders(request, clientId);
                request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
                using var response = await client.SendAsync(request, cancellationToken);
                if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                {
                    return new ConnectionTestResult(false, null, null, "The server rejected the token.");
                }
                if (!response.IsSuccessStatusCode)
                {
                    return new ConnectionTestResult(false, null, null, $"Server returned {(int)response.StatusCode}.");
                }

                var info = MediaServerInfoParser.ParsePlex(await response.Content.ReadAsStringAsync(cancellationToken));
                return info is null
                    ? new ConnectionTestResult(false, null, null, "Reached the URL but it does not look like a Plex server.")
                    : new ConnectionTestResult(true, info.Name, info.Version, null);
            }

            using var jfRequest = new HttpRequestMessage(HttpMethod.Get, $"{root}/System/Info");
            AddJellyfinHeaders(jfRequest, await settings.GetOrCreatePlexClientIdentifierAsync(cancellationToken), token);
            jfRequest.Headers.TryAddWithoutValidation("X-Emby-Token", token);
            using var jfResponse = await client.SendAsync(jfRequest, cancellationToken);
            if (jfResponse.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                return new ConnectionTestResult(false, null, null, "The server rejected the API key.");
            }
            if (!jfResponse.IsSuccessStatusCode)
            {
                return new ConnectionTestResult(false, null, null, $"Server returned {(int)jfResponse.StatusCode}.");
            }

            var jfInfo = MediaServerInfoParser.ParseJellyfin(await jfResponse.Content.ReadAsStringAsync(cancellationToken));
            return jfInfo is null
                ? new ConnectionTestResult(false, null, null, $"Reached the URL but it does not look like a {type} server.")
                : new ConnectionTestResult(true, jfInfo.Name, jfInfo.Version, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ConnectionTestResult(false, null, null, $"Could not reach the server: {ex.Message}");
        }
    }

    private static void AddPlexHeaders(HttpRequestMessage request, string clientId)
    {
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("X-Plex-Product", Product);
        request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", clientId);
    }

    private static void AddJellyfinHeaders(HttpRequestMessage request, string deviceId, string? token)
    {
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        var auth = $"MediaBrowser Client=\"{Product}\", Device=\"{Product}\", DeviceId=\"{deviceId}\", Version=\"1.0\"";
        if (!string.IsNullOrEmpty(token))
        {
            auth += $", Token=\"{token}\"";
        }
        request.Headers.TryAddWithoutValidation("Authorization", auth);
    }

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
