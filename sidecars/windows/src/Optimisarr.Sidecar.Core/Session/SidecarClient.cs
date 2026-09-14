using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Optimisarr.Sidecar.Core.Capabilities;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>Why a pairing or check-in did not succeed, in terms an operator can act on.</summary>
public sealed class SidecarException(string message, bool recoverable) : Exception(message)
{
    /// <summary>
    /// True when trying again later could work — the server being unreachable or the feature being
    /// switched off. False when only a person can fix it: a spent PIN, a revoked credential, an
    /// incompatible protocol. The service loop uses this to decide whether to keep beating or stop.
    /// </summary>
    public bool Recoverable { get; } = recoverable;
}

/// <summary>What the server returns when a PIN is redeemed. The credential arrives exactly once.</summary>
public sealed record PairingResult(int WorkerId, string Credential, int ProtocolVersion);

/// <summary>
/// The acknowledgement of a check-in. The interval comes from the control plane so this service
/// paces itself from the server rather than hard-coding a value that could drift out of step with
/// the server's own offline threshold.
/// </summary>
public sealed record HeartbeatResult(
    int WorkerId,
    int ProtocolVersion,
    TimeSpan Interval,
    bool Draining);

/// <summary>
/// The HTTP half of the worker protocol: redeem a PIN, then check in.
///
/// Deliberately free of any Windows service machinery so the whole conversation can be tested
/// against a fake handler rather than a live server.
/// </summary>
public sealed class SidecarClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Redeems a PIN and returns the credential. The PIN is single-use: a failure here generally
    /// means the operator must issue a new one rather than retry this call.
    /// </summary>
    public async Task<PairingResult> PairAsync(
        string serverAddress,
        string pin,
        SidecarCapabilities capabilities,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            // Sent as the operator typed it. The server tolerates the grouping spaces people read
            // aloud, so stripping them here would only risk mangling a valid code.
            ["code"] = pin,
            ["name"] = capabilities.Name,
            ["operatingSystem"] = capabilities.OperatingSystem,
            ["architecture"] = capabilities.Architecture,
            ["protocolMinimum"] = WorkerProtocol.Minimum,
            ["protocolMaximum"] = WorkerProtocol.Maximum,
            ["videoEncoders"] = capabilities.VideoEncoders,
            ["audioEncoders"] = capabilities.AudioEncoders,
            ["hardwareDecoders"] = capabilities.HardwareDecoders,
            ["vmaf"] = capabilities.Vmaf.ToString(),
            ["freeScratchBytes"] = capabilities.FreeScratchBytes,
            ["maxConcurrency"] = capabilities.MaxConcurrency,
            ["sidecarVersion"] = SidecarBuild.Version,
        };

        using var response = await http.PostAsJsonAsync(
            Endpoint(serverAddress, "/api/workers/pair"), body, Json, cancellationToken);

        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                var result = await response.Content.ReadFromJsonAsync<PairingResult>(Json, cancellationToken);
                return result ?? throw new SidecarException(
                    "The server accepted the pairing code but its reply could not be read.", recoverable: false);

            case HttpStatusCode.Unauthorized:
                throw new SidecarException(
                    await MessageAsync(response, "That pairing code was not accepted.", cancellationToken),
                    recoverable: false);

            case HttpStatusCode.Forbidden:
                throw new SidecarException(
                    await MessageAsync(response, "Remote workers are turned off on the server.", cancellationToken),
                    recoverable: true);

            case HttpStatusCode.Conflict:
                throw new SidecarException(
                    await MessageAsync(response, "This sidecar speaks no protocol version the server accepts.", cancellationToken),
                    recoverable: false);

            default:
                throw new SidecarException(
                    $"The server replied unexpectedly (HTTP {(int)response.StatusCode}).", recoverable: true);
        }
    }

    /// <summary>
    /// Reports in, and says again what this machine can do and how busy it is.
    ///
    /// Capabilities ride along rather than being sent only at pairing: a machine changes — FFmpeg
    /// is rebuilt, a driver stops working, an encoder that used to open no longer does — and
    /// without this the server would go on scheduling against whatever was true the day the two
    /// were introduced.
    /// </summary>
    public async Task<HeartbeatResult> HeartbeatAsync(
        StoredPairing pairing,
        SidecarCapabilities capabilities,
        MachineLoad? load = null,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["freeScratchBytes"] = capabilities.FreeScratchBytes,
            ["maxConcurrency"] = capabilities.MaxConcurrency,
            ["videoEncoders"] = capabilities.VideoEncoders,
            ["audioEncoders"] = capabilities.AudioEncoders,
            ["hardwareDecoders"] = capabilities.HardwareDecoders,
            ["vmaf"] = capabilities.Vmaf.ToString(),
            // Every check-in, not only at pairing: upgrading this service does not re-pair it, so a
            // version recorded once would be wrong from the first upgrade onwards.
            ["sidecarVersion"] = SidecarBuild.Version,
        };

        // Only what was actually measured. A machine whose counters could not be read sends
        // nothing rather than a zero the Workers tab would draw as an idle machine.
        if (load?.CpuBusyFraction is { } cpu)
        {
            body["cpuBusyFraction"] = cpu;
        }
        if (load?.GpuBusyFraction is { } gpu)
        {
            body["gpuBusyFraction"] = gpu;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post, Endpoint(pairing.ServerAddress, "/api/workers/heartbeat"))
        {
            Content = JsonContent.Create(body, options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pairing.Credential);

        using var response = await http.SendAsync(request, cancellationToken);
        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                var payload = await response.Content.ReadFromJsonAsync<HeartbeatResponse>(Json, cancellationToken)
                    ?? throw new SidecarException("The server's check-in reply could not be read.", recoverable: true);
                return new HeartbeatResult(
                    payload.WorkerId,
                    payload.ProtocolVersion,
                    TimeSpan.FromSeconds(Math.Max(5, payload.HeartbeatIntervalSeconds)),
                    payload.Draining);

            case HttpStatusCode.Unauthorized:
                // Covers absent, malformed, unknown and revoked credentials alike. Only pairing
                // again fixes it, so this must not be retried in a loop.
                throw new SidecarException(
                    "The server no longer recognises this worker's credential. Pair again.",
                    recoverable: false);

            case HttpStatusCode.Forbidden:
                throw new SidecarException(
                    await MessageAsync(response, "Remote workers are turned off on the server.", cancellationToken),
                    recoverable: true);

            default:
                throw new SidecarException(
                    $"The server replied unexpectedly (HTTP {(int)response.StatusCode}).", recoverable: true);
        }
    }

    private sealed record HeartbeatResponse(
        int WorkerId,
        int ProtocolVersion,
        DateTimeOffset ServerTimeUtc,
        int HeartbeatIntervalSeconds,
        bool Draining);

    /// <summary>
    /// The server's machine-readable errors carry a human sentence in <c>error</c>. Surfacing that
    /// rather than inventing wording keeps what an operator reads here identical to what the
    /// server would have told them.
    /// </summary>
    private static async Task<string> MessageAsync(
        HttpResponseMessage response, string fallback, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.TryGetProperty("error", out var error)
                && error.GetString() is { Length: > 0 } message
                    ? message
                    : fallback;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// Builds an absolute URL from whatever the operator typed.
    ///
    /// An address with no scheme is reached over http, which is right for a server on a home
    /// network and wrong for one behind a TLS proxy. Callers report the address they were given
    /// when this fails, so the scheme is visible as a possible cause rather than left to guesswork.
    /// </summary>
    internal static Uri Endpoint(string serverAddress, string path)
    {
        var trimmed = serverAddress.Trim();
        if (trimmed.Length == 0)
        {
            throw new SidecarException("No server address was given.", recoverable: false);
        }

        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }

        trimmed = trimmed.TrimEnd('/');
        return Uri.TryCreate(trimmed + path, UriKind.Absolute, out var uri)
            ? uri
            : throw new SidecarException($"'{serverAddress}' is not a usable server address.", recoverable: false);
    }
}
