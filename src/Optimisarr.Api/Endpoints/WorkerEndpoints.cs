using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Api.Workers;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Api.Endpoints;

/// <summary>What an operator sees about a paired sidecar. Never carries the credential fingerprint.</summary>
internal sealed record WorkerDto(
    int Id,
    string Name,
    string OperatingSystem,
    string Architecture,
    int ProtocolVersion,
    IReadOnlyList<string> VideoEncoders,
    IReadOnlyList<string> HardwareDecoders,
    string Vmaf,
    long FreeScratchBytes,
    int MaxConcurrency,
    DateTimeOffset PairedAt,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? RevokedAt,
    bool Online);

/// <summary>The PIN an operator reads off the screen and types into a sidecar.</summary>
internal sealed record PairingCodeDto(string Code, DateTimeOffset ExpiresUtc, int AttemptsRemaining);

/// <summary>
/// What a sidecar sends to redeem a PIN. Everything except the code is self-reported.
///
/// <paramref name="Vmaf"/> is a name rather than a number. This contract is implemented by
/// separately-versioned third-party sidecars, so an enum's meaning must never depend on its
/// ordinal: inserting a member would otherwise silently change what an existing worker is believed
/// to support, and that value gates whether a job may be offered to it.
/// </summary>
internal sealed record PairRequest(
    string? Code,
    string? Name,
    string? OperatingSystem,
    string? Architecture,
    int ProtocolMinimum,
    int ProtocolMaximum,
    IReadOnlyList<string>? VideoEncoders,
    IReadOnlyList<string>? HardwareDecoders,
    string? Vmaf,
    long FreeScratchBytes,
    int MaxConcurrency);

/// <summary>The credential, returned exactly once. Optimisarr keeps only its fingerprint.</summary>
internal sealed record PairResponse(int WorkerId, string Credential, int ProtocolVersion);

/// <summary>
/// What a sidecar reports when it checks in. Only the volatile numbers: free scratch space and how
/// much work it will currently accept. Encoders and VMAF support are settled at pairing, because a
/// worker quietly changing what it claims to support between assignments is a capability the
/// control plane should re-establish deliberately, not absorb from a heartbeat.
/// </summary>
internal sealed record HeartbeatRequest(long FreeScratchBytes, int MaxConcurrency);

/// <summary>
/// The acknowledgement. Carries the interval so a sidecar paces itself from the control plane
/// rather than hard-coding a value that could drift out of step with the server's threshold.
/// </summary>
internal sealed record HeartbeatResponse(
    int WorkerId,
    int ProtocolVersion,
    DateTimeOffset ServerTimeUtc,
    int HeartbeatIntervalSeconds);

internal static class WorkerEndpoints
{
    public static void MapWorkerEndpoints(this WebApplication app)
    {
        // Issue a PIN for the operator to read out. Replaces any previous one so only the code on
        // screen is live.
        app.MapPost("/api/workers/pairing-code", async (
            WorkerPairingService pairing,
            SettingsStore settings,
            CancellationToken cancellationToken) =>
        {
            if (await WorkerGate.RefusedAsync(settings, cancellationToken) is { } refused)
            {
                return refused;
            }

            var code = pairing.Issue(DateTimeOffset.UtcNow);
            return Results.Ok(new PairingCodeDto(code.Code, code.ExpiresUtc, PairingCode.MaxAttempts));
        })
        .WithName("IssueWorkerPairingCode")
        .Produces<PairingCodeDto>();

        // The PIN currently on screen, so a reloaded UI can resume its countdown. Having no code
        // live is the ordinary resting state rather than a failure, so it answers 204 rather than
        // 404 — a caller should not have to treat "nothing to show" as an error.
        app.MapGet("/api/workers/pairing-code", (WorkerPairingService pairing) =>
        {
            var code = pairing.Active(DateTimeOffset.UtcNow);
            return code is null
                ? Results.NoContent()
                : Results.Ok(new PairingCodeDto(
                    code.Code, code.ExpiresUtc, PairingCode.MaxAttempts - code.FailedAttempts));
        })
        .WithName("ActiveWorkerPairingCode")
        .Produces<PairingCodeDto>();

        app.MapDelete("/api/workers/pairing-code", (WorkerPairingService pairing) =>
        {
            pairing.Cancel();
            return Results.NoContent();
        })
        .WithName("CancelWorkerPairingCode");

        // The one route a sidecar can reach without an admin token; the PIN authenticates it.
        app.MapPost("/api/workers/pair", async (
            PairRequest request,
            WorkerPairingService pairing,
            SettingsStore settings,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (await WorkerGate.RefusedAsync(settings, cancellationToken) is { } refused)
            {
                return refused;
            }

            // Authenticate before doing anything else. A wrong or dead code spends an attempt and
            // learns nothing about the server.
            var redemption = pairing.Redeem(request.Code ?? string.Empty, DateTimeOffset.UtcNow);
            if (redemption != PairingRedemption.Accepted)
            {
                return Results.Json(
                    new ApiError($"worker.pairing.{char.ToLowerInvariant(redemption.ToString()[0])}{redemption.ToString()[1..]}",
                        RedemptionMessage(redemption)),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            // The code is spent from here on, including when negotiation fails below. That is
            // intentional: one PIN buys one pairing attempt, so an incompatible sidecar cannot
            // probe protocol versions repeatedly on a single code.
            var negotiation = WorkerProtocol.Negotiate(request.ProtocolMinimum, request.ProtocolMaximum);
            if (!negotiation.Compatible)
            {
                return ApiErrors.Conflict("worker.protocol.incompatible", negotiation.Reason!);
            }

            if (!TryParseVmaf(request.Vmaf, out var vmaf))
            {
                return ApiErrors.BadRequest("worker.vmaf.invalid",
                    $"Unknown VMAF capability '{request.Vmaf}'. Valid values: " +
                    $"{string.Join(", ", Enum.GetNames<VmafCapability>())}.",
                    new { value = request.Vmaf });
            }

            var name = string.IsNullOrWhiteSpace(request.Name) ? "Unnamed worker" : request.Name.Trim();
            var credential = WorkerCredential.Issue();

            var worker = new Worker
            {
                Name = name.Length > 160 ? name[..160] : name,
                OperatingSystem = Trimmed(request.OperatingSystem, 32),
                Architecture = Trimmed(request.Architecture, 32),
                ProtocolVersion = negotiation.AgreedVersion,
                VideoEncoders = Join(request.VideoEncoders),
                HardwareDecoders = Join(request.HardwareDecoders),
                Vmaf = vmaf,
                FreeScratchBytes = Math.Max(0, request.FreeScratchBytes),
                MaxConcurrency = Math.Max(0, request.MaxConcurrency),
                CredentialFingerprint = WorkerCredential.Fingerprint(credential),
                PairedAt = DateTimeOffset.UtcNow,
                // Pairing is itself a check-in — we just heard from the machine. Without this a
                // freshly paired worker would read as offline until its first heartbeat, and be
                // refused work for no reason during that window.
                LastSeenAt = DateTimeOffset.UtcNow
            };

            db.Workers.Add(worker);
            await db.SaveChangesAsync(cancellationToken);

            // The only time the credential leaves this process. Optimisarr cannot reproduce it.
            return Results.Ok(new PairResponse(worker.Id, credential, negotiation.AgreedVersion));
        })
        .WithName("PairWorker")
        .Produces<PairResponse>()
        // Declared here because the document transformer only annotates admin-token protection,
        // and this route is outside it. A sidecar client generated from the spec still needs to
        // know a wrong, spent, expired, or burned PIN answers 401.
        .Produces<ApiError>(StatusCodes.Status401Unauthorized);

        // A paired sidecar checking in. Authenticated by its own credential, not the admin token,
        // so this route is open in the same way /pair is — but where /pair is guarded by a
        // short-lived PIN, this one is guarded by 32 random bytes.
        app.MapPost("/api/workers/heartbeat", async (
            HeartbeatRequest request,
            HttpRequest http,
            SettingsStore settings,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            // Refused before the credential is even looked at: turning the feature off must stop
            // check-ins outright, not merely stop new pairings.
            if (await WorkerGate.RefusedAsync(settings, cancellationToken) is { } refused)
            {
                return refused;
            }

            var worker = await WorkerAuth.ResolveAsync(http, db, cancellationToken);
            if (worker is null)
            {
                // Covers absent, malformed, unknown, and revoked credentials alike. A revoked
                // worker gets exactly this, which is what makes revocation bite.
                return Results.Json(
                    new ApiError("worker.credential.invalid", "Unknown or revoked worker credential."),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            // Stamped from the server's clock, never from the request, so a sidecar with a wrong
            // or dishonest clock cannot claim to have been alive.
            worker.LastSeenAt = DateTimeOffset.UtcNow;
            worker.FreeScratchBytes = Math.Max(0, request.FreeScratchBytes);
            worker.MaxConcurrency = Math.Max(0, request.MaxConcurrency);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(new HeartbeatResponse(
                worker.Id,
                worker.ProtocolVersion,
                worker.LastSeenAt.Value,
                (int)WorkerLiveness.HeartbeatInterval.TotalSeconds));
        })
        .WithName("WorkerHeartbeat")
        .Produces<HeartbeatResponse>()
        // Likewise: an absent, unknown, or revoked worker credential answers 401 here, and that
        // belongs in the contract a sidecar is built against.
        .Produces<ApiError>(StatusCodes.Status401Unauthorized);

        app.MapGet("/api/workers", async (OptimisarrDbContext db, CancellationToken cancellationToken) =>
        {
            var workers = await db.Workers
                .AsNoTracking()
                .OrderBy(worker => worker.Id)
                .ToListAsync(cancellationToken);

            var now = DateTimeOffset.UtcNow;
            return Results.Ok(workers.Select(w => ToDto(w, now)).ToList());
        })
        .WithName("ListWorkers")
        .Produces<IReadOnlyList<WorkerDto>>();

        // Revocation clears the fingerprint rather than deleting the row, so the pairing stays in
        // the audit trail while the credential stops matching anything at all.
        app.MapDelete("/api/workers/{id:int}", async (
            int id,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            var worker = await db.Workers.FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
            if (worker is null)
            {
                return ApiErrors.NotFound("worker.notFound", $"No worker with id {id}.", new { id });
            }

            worker.CredentialFingerprint = null;
            worker.RevokedAt = DateTimeOffset.UtcNow;
            worker.MaxConcurrency = 0;
            await db.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        })
        .WithName("RevokeWorker");
    }

    private static string RedemptionMessage(PairingRedemption redemption) => redemption switch
    {
        PairingRedemption.Expired => "That pairing code has expired. Generate a new one in Optimisarr.",
        PairingRedemption.AlreadyRedeemed => "That pairing code has already been used.",
        PairingRedemption.TooManyAttempts =>
            "That pairing code was entered incorrectly too many times and is no longer valid. Generate a new one.",
        _ => "That pairing code is not correct."
    };

    private static WorkerDto ToDto(Worker worker, DateTimeOffset nowUtc) => new(
        worker.Id,
        worker.Name,
        worker.OperatingSystem,
        worker.Architecture,
        worker.ProtocolVersion,
        Split(worker.VideoEncoders),
        Split(worker.HardwareDecoders),
        worker.Vmaf.ToString(),
        worker.FreeScratchBytes,
        worker.MaxConcurrency,
        worker.PairedAt,
        worker.LastSeenAt,
        worker.RevokedAt,
        // Revoked workers are never "online" whatever their last heartbeat said, so the UI cannot
        // show a green light next to a worker that can no longer authenticate.
        worker.RevokedAt is null && WorkerLiveness.IsOnline(worker.LastSeenAt, nowUtc));

    /// <summary>
    /// Accepts a capability name, case-insensitively. An absent value means the worker claims no
    /// VMAF support, which is a real answer rather than a missing one; anything unrecognised is
    /// refused so a typo cannot quietly downgrade to None.
    /// </summary>
    private static bool TryParseVmaf(string? value, out VmafCapability parsed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parsed = VmafCapability.None;
            return true;
        }

        return Enum.TryParse(value.Trim(), ignoreCase: true, out parsed)
            && Enum.IsDefined(parsed);
    }

    private static string Trimmed(string? value, int max)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }

    private static string Join(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return string.Empty;
        }

        var cleaned = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Take(64);

        var joined = string.Join(',', cleaned);
        return joined.Length > 1024 ? joined[..joined.LastIndexOf(',', 1023)] : joined;
    }

    private static IReadOnlyList<string> Split(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
