using System.Text;
using Optimisarr.Api.Library;
using Optimisarr.Core.Activity;

namespace Optimisarr.Api.Replacement;

/// <summary>Where to send the user, and how to poll, for a Plex sign-in.</summary>
public sealed record PlexConnectStart(long Id, string Code, string AuthUrl);

/// <summary>The Quick Connect code to show the user and the secret to poll with.</summary>
public sealed record JellyfinConnectStart(string Code, string Secret);

/// <summary>The result of polling a connect flow: authorised yet, and the token once it is.</summary>
public sealed record ConnectResult(bool Authorized, string? Token);

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
