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
    /// <summary>
    /// The options every request and response is read with. Internal so a test can decode a real
    /// server payload exactly as this client would, which is the check that was missing when a
    /// well-formed quality search was dropped on the other sidecar and nobody noticed for a week.
    /// </summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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
            ["protocolMinimum"] = WorkerProtocol.Minimum,
            ["protocolMaximum"] = WorkerProtocol.Maximum,
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

    /// <summary>
    /// Asks for work. Returns null when there is nothing for this machine, which is the ordinary
    /// answer rather than a failure: the server matches a job against what this worker proved it
    /// can do, and most of the time nothing matches or nothing is waiting.
    /// </summary>
    public async Task<Assignment?> ClaimAsync(
        StoredPairing pairing, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, Endpoint(pairing.ServerAddress, "/api/workers/claim"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pairing.Credential);

        using var response = await http.SendAsync(request, cancellationToken);
        return response.StatusCode switch
        {
            HttpStatusCode.NoContent => null,
            HttpStatusCode.OK => await response.Content.ReadFromJsonAsync<Assignment>(Json, cancellationToken),
            HttpStatusCode.Unauthorized => throw new SidecarException(
                "The server no longer recognises this worker's credential. Pair again.", recoverable: false),
            _ => throw new SidecarException(
                $"The server replied unexpectedly to a claim (HTTP {(int)response.StatusCode}).", recoverable: true),
        };
    }

    /// <summary>
    /// Extends a lease, and says where this machine has got to.
    ///
    /// <para>A lapsed lease means the job has been handed to somebody else, so this failing is not
    /// a transient to shrug off: whatever is running locally should stop rather than spend an hour
    /// encoding something the server has already reassigned.</para>
    /// </summary>
    public async Task RenewAsync(
        StoredPairing pairing,
        Guid leaseId,
        RemoteStage stage,
        double? encodedSeconds = null,
        MachineLoad? load = null,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?> { ["stage"] = stage.ToString() };
        if (encodedSeconds is { } seconds)
        {
            body["encodedSeconds"] = seconds;
        }
        // Carried here as well as on the check-in: a renewal happens every few seconds while a job
        // runs, so this is the figure an operator watching an encode actually sees.
        if (load?.CpuBusyFraction is { } cpu)
        {
            body["cpuBusyFraction"] = cpu;
        }
        if (load?.GpuBusyFraction is { } gpu)
        {
            body["gpuBusyFraction"] = gpu;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post, Endpoint(pairing.ServerAddress, $"/api/workers/leases/{leaseId}/renew"))
        {
            Content = JsonContent.Create(body, options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pairing.Credential);

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw new SidecarException(
            response.StatusCode switch
            {
                HttpStatusCode.Conflict => "That lease has lapsed and the job may have been reassigned.",
                HttpStatusCode.Forbidden => "That lease belongs to another worker.",
                HttpStatusCode.Unauthorized => "This worker's credential was rejected.",
                _ => $"Renewing the lease failed (HTTP {(int)response.StatusCode}).",
            },
            // Only the answers that say the lease is no longer this worker's are final: the work
            // is void and carrying on would only burn electricity. Everything else is the server
            // having a moment — a 502 from the proxy while the container restarts is the common
            // one, and it used to throw away whatever encode was running at the time.
            recoverable: response.StatusCode is not (
                HttpStatusCode.Conflict or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized));
    }

    /// <summary>
    /// Reports what this machine measured for one candidate, and returns what to do next.
    ///
    /// <para>No verdict is sent, only the evidence: whether a candidate met the target needs the
    /// library's policy and the pooling rules, and both live on the control plane. This machine
    /// encodes, scores, and says what it saw.</para>
    /// </summary>
    public async Task<AdaptiveSearchDirection> ReportAdaptiveProbeAsync(
        StoredPairing pairing,
        Guid leaseId,
        int quality,
        IReadOnlyList<long> windowEncodedBytes,
        IReadOnlyList<string> logs,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            Endpoint(pairing.ServerAddress, $"/api/workers/leases/{leaseId}/quality-probe"))
        {
            Content = JsonContent.Create(
                // The total is what every server reads; the split is what lets a newer one say
                // which scenes grew. A server that predates the split ignores it.
                new { quality, encodedBytes = windowEncodedBytes.Sum(), windowEncodedBytes, logs },
                options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pairing.Credential);

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            return await response.Content.ReadFromJsonAsync<AdaptiveSearchDirection>(Json, cancellationToken)
                ?? throw new SidecarException(
                    "The server answered the measurement in a form this sidecar could not read.",
                    recoverable: false);
        }

        throw new SidecarException(
            response.StatusCode switch
            {
                // The lease is gone and the search with it: the evidence is bound to this machine's
                // encoder, so the next holder starts again rather than inheriting half a search.
                HttpStatusCode.Conflict => "That lease is no longer held, so the search cannot continue.",
                HttpStatusCode.Forbidden => "That lease belongs to another worker.",
                _ => $"Reporting a measurement failed (HTTP {(int)response.StatusCode}).",
            },
            recoverable: false);
    }

    /// <summary>
    /// Offers the server this machine's measurement of the finished candidate, bound to both
    /// hashes so it can only ever be read as evidence about these exact bytes.
    ///
    /// <para>An offer, not a claim. A refusal is not a job failure: the server simply measures for
    /// itself, which is what it did before any worker could measure at all. So this reports the
    /// outcome rather than throwing — a candidate that encoded perfectly well must not be handed
    /// back because the server would not take a score for it.</para>
    /// </summary>
    public async Task ReportVerificationAsync(
        StoredPairing pairing, Guid leaseId, Optimisarr.Core.Workers.RemoteVerificationEvidence evidence,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            Endpoint(pairing.ServerAddress, $"/api/workers/leases/{leaseId}/verification"))
        {
            Content = JsonContent.Create(evidence, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pairing.Credential);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new SidecarException($"Full verification evidence was refused (HTTP {(int)response.StatusCode}).", recoverable: false);
    }

    public async Task<bool> ReportQualityAsync(
        StoredPairing pairing,
        Guid leaseId,
        string sourceSha256,
        string candidateSha256,
        IReadOnlyList<string> logs,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, Endpoint(pairing.ServerAddress, $"/api/workers/leases/{leaseId}/quality"))
        {
            Content = JsonContent.Create(new { sourceSha256, candidateSha256, logs }, options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pairing.Credential);

        using var response = await http.SendAsync(request, cancellationToken);
        return response.StatusCode == HttpStatusCode.OK;
    }

    /// <summary>Gives a job back, so it returns to the queue at once rather than waiting to lapse.</summary>
    public async Task ReleaseAsync(
        StoredPairing pairing, Guid leaseId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, Endpoint(pairing.ServerAddress, $"/api/workers/leases/{leaseId}/release"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pairing.Credential);
        using var response = await http.SendAsync(request, cancellationToken);
        // A shutdown request must distinguish an acknowledged hand-back from a lease that may
        // still be held. The runner preserves its original failure reason in the returned outcome.
        response.EnsureSuccessStatusCode();
    }

    public async Task ReportSizeBudgetExceededAsync(
        StoredPairing pairing, Guid leaseId, long observedBytes, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            Endpoint(pairing.ServerAddress, $"/api/workers/leases/{leaseId}/size-budget-exceeded"))
        {
            Content = JsonContent.Create(new { observedBytes })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pairing.Credential);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task ReportSizeBudgetUndershotAsync(
        StoredPairing pairing, Guid leaseId, long observedBytes, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            Endpoint(pairing.ServerAddress, $"/api/workers/leases/{leaseId}/size-budget-undershot"))
        {
            Content = JsonContent.Create(new { observedBytes })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pairing.Credential);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
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
