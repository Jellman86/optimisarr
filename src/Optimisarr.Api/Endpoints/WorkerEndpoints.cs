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
    /// <summary>The sidecar's own build, as it reported it. Empty when it does not report one.</summary>
    string SidecarVersion,
    IReadOnlyList<string> VideoEncoders,
    IReadOnlyList<string> AudioEncoders,
    IReadOnlyList<string> HardwareDecoders,
    string Vmaf,
    long FreeScratchBytes,
    int MaxConcurrency,
    /// <summary>How busy the worker's machine last said it was, 0-1; null when it has not said.</summary>
    double? CpuBusyFraction,
    /// <summary>
    /// The worker's accelerator utilisation, 0-1, or null. Low does not mean unused: a dedicated
    /// media engine — Apple silicon's VideoToolbox encoder among them — does not appear here.
    /// </summary>
    double? GpuBusyFraction,
    /// <summary>When those two were reported, so a stale reading is not drawn as current.</summary>
    DateTimeOffset? LoadReportedAt,
    DateTimeOffset PairedAt,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? RevokedAt,
    bool Online,
    /// <summary>When an operator asked this worker to stop taking work; null while it takes work.</summary>
    DateTimeOffset? DrainRequestedAt,
    /// <summary>Leases this worker holds right now: the jobs a drain is waiting on.</summary>
    int HeldLeases,
    /// <summary>The jobs behind those leases, with where the worker says it is on each.</summary>
    IReadOnlyList<WorkerJobDto> ActiveJobs,
    /// <summary>The most recent thing the server refused or discarded from this worker; null if nothing yet.</summary>
    string? LastProblem,
    DateTimeOffset? LastProblemAt);

/// <summary>One job a worker holds. Stage is "Claimed" until the worker first says otherwise.</summary>
internal sealed record WorkerJobDto(int JobId, string? RelativePath, string Stage, double Progress);

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
    IReadOnlyList<string>? AudioEncoders,
    IReadOnlyList<string>? HardwareDecoders,
    string? Vmaf,
    long FreeScratchBytes,
    int MaxConcurrency,
    /// <summary>
    /// The sidecar's own build. Optional, so a sidecar written against the earlier contract still
    /// pairs; it is recorded and displayed, never used to decide what a worker may be offered.
    /// </summary>
    string? SidecarVersion = null);

/// <summary>The credential, returned exactly once. Optimisarr keeps only its fingerprint.</summary>
internal sealed record PairResponse(int WorkerId, string Credential, int ProtocolVersion);

/// <summary>
/// What a sidecar reports when it checks in. Only the volatile numbers: free scratch space and how
/// much work it will currently accept. Encoders and VMAF support are settled at pairing, because a
/// worker quietly changing what it claims to support between assignments is a capability the
/// control plane should re-establish deliberately, not absorb from a heartbeat.
/// </summary>
/// <summary>
/// A check-in. Capabilities ride along because a machine changes: FFmpeg is rebuilt, a driver
/// stops working, an encoder that used to open no longer does. They were previously recorded only
/// at pairing, so a sidecar that re-probed itself at launch could not tell the server, and the
/// server went on scheduling against what was true the day the two were introduced. The capability
/// fields are optional so an older sidecar still checks in; what it omits is left as it was.
/// </summary>
internal sealed record HeartbeatRequest(
    long FreeScratchBytes,
    int MaxConcurrency,
    IReadOnlyList<string>? VideoEncoders = null,
    IReadOnlyList<string>? AudioEncoders = null,
    IReadOnlyList<string>? HardwareDecoders = null,
    string? Vmaf = null,
    /// <summary>
    /// How busy the machine is, 0-1, each optional and separately so: a worker can report its CPU
    /// while having no readable accelerator. Absent leaves whatever was last reported, so a
    /// momentary failure to read does not erase a good figure.
    /// </summary>
    double? CpuBusyFraction = null,
    double? GpuBusyFraction = null,
    /// <summary>
    /// The sidecar's own build, repeated on every check-in rather than only at pairing — upgrading
    /// a sidecar does not re-pair it, so a version recorded once would be wrong from the first
    /// upgrade onwards and quietly stay wrong.
    /// </summary>
    string? SidecarVersion = null,
    int? ProtocolMinimum = null,
    int? ProtocolMaximum = null);

/// <summary>
/// The acknowledgement. Carries the interval so a sidecar paces itself from the control plane
/// rather than hard-coding a value that could drift out of step with the server's threshold.
/// </summary>
internal sealed record HeartbeatResponse(
    int WorkerId,
    int ProtocolVersion,
    DateTimeOffset ServerTimeUtc,
    int HeartbeatIntervalSeconds,
    /// <summary>True while an operator has asked the worker to finish what it holds and take no more.</summary>
    bool Draining);

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
                SidecarVersion = Trimmed(request.SidecarVersion, 64),
                VideoEncoders = Join(request.VideoEncoders),
                AudioEncoders = Join(request.AudioEncoders),
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

            // Upgrades keep their pairing. Omitted ranges identify legacy protocol-1 clients,
            // including a downgrade, so they must never inherit a newer client's capabilities.
            var negotiation = WorkerProtocol.Negotiate(request.ProtocolMinimum ?? 1, request.ProtocolMaximum ?? 1);
            if (!negotiation.Compatible)
                return ApiErrors.Conflict("worker.protocol.incompatible", negotiation.Reason!);
            worker.ProtocolVersion = negotiation.AgreedVersion;

            // Stamped from the server's clock, never from the request, so a sidecar with a wrong
            // or dishonest clock cannot claim to have been alive.
            worker.LastSeenAt = DateTimeOffset.UtcNow;
            worker.FreeScratchBytes = Math.Max(0, request.FreeScratchBytes);
            worker.MaxConcurrency = Math.Max(0, request.MaxConcurrency);

            // Only what the sidecar actually sent. An older one omits these, and overwriting its
            // recorded capabilities with nothing would silently drain a working worker.
            if (request.VideoEncoders is not null)
            {
                worker.VideoEncoders = Join(request.VideoEncoders);
            }
            if (request.AudioEncoders is not null)
            {
                worker.AudioEncoders = Join(request.AudioEncoders);
            }
            if (request.HardwareDecoders is not null)
            {
                worker.HardwareDecoders = Join(request.HardwareDecoders);
            }
            if (request.Vmaf is not null && Enum.TryParse<VmafCapability>(request.Vmaf, true, out var vmaf))
            {
                worker.Vmaf = vmaf;
            }
            // Recorded on every check-in so an upgrade shows up without re-pairing, but only when
            // the sidecar actually said something: an older one omits this, and blanking what a
            // previous check-in reported would lose the answer rather than refresh it.
            if (request.SidecarVersion is not null)
            {
                worker.SidecarVersion = Trimmed(request.SidecarVersion, 64);
            }
            RecordLoad(worker, request.CpuBusyFraction, request.GpuBusyFraction);

            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(new HeartbeatResponse(
                worker.Id,
                worker.ProtocolVersion,
                worker.LastSeenAt.Value,
                (int)WorkerLiveness.HeartbeatInterval.TotalSeconds,
                worker.DrainRequestedAt is not null));
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

            var active = await ActiveJobsByWorkerAsync(db, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            return Results.Ok(workers
                .Select(w => ToDto(w, now, active.GetValueOrDefault(w.Id) ?? []))
                .ToList());
        })
        .WithName("ListWorkers")
        .Produces<IReadOnlyList<WorkerDto>>();

        // Draining is a claim refusal and nothing more: the worker keeps what it holds, finishes
        // and delivers it, and is offered nothing new until resumed. Idempotent both ways, so an
        // operator clicking twice, or a retried request, cannot flip the state back.
        app.MapPost("/api/workers/{id:int}/drain", async (
            int id,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            var worker = await db.Workers.FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
            if (worker is null)
            {
                return ApiErrors.NotFound("worker.notFound", $"No worker with id {id}.", new { id });
            }

            worker.DrainRequestedAt ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return await WorkerRowAsync(worker, db, cancellationToken);
        })
        .WithName("DrainWorker")
        .Produces<WorkerDto>()
        .Produces<ApiError>(StatusCodes.Status404NotFound);

        app.MapDelete("/api/workers/{id:int}/drain", async (
            int id,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            var worker = await db.Workers.FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
            if (worker is null)
            {
                return ApiErrors.NotFound("worker.notFound", $"No worker with id {id}.", new { id });
            }

            // A revoked worker cannot authenticate, so "resume" would promise work it can never
            // claim. Re-pairing is the only way back, and this says so.
            if (worker.RevokedAt is not null)
            {
                return Results.Json(
                    new ApiError("worker.revoked", "A revoked worker cannot resume; pair it again."),
                    statusCode: StatusCodes.Status409Conflict);
            }

            worker.DrainRequestedAt = null;
            await db.SaveChangesAsync(cancellationToken);
            return await WorkerRowAsync(worker, db, cancellationToken);
        })
        .WithName("ResumeWorker")
        .Produces<WorkerDto>()
        .Produces<ApiError>(StatusCodes.Status404NotFound)
        .Produces<ApiError>(StatusCodes.Status409Conflict);

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

        // Revoking keeps the row, which is right for a worker turned off deliberately: the audit
        // trail of what it once held outlives it. It is wrong for an orphan — pairing the same
        // machine again leaves the old record on the Workers tab for ever with nothing that clears
        // it — so removal is a separate, deliberate act rather than a second meaning for DELETE.
        app.MapPost("/api/workers/{id:int}/forget", async (
            int id,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            var worker = await db.Workers.FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
            if (worker is null)
            {
                return ApiErrors.NotFound("worker.notFound", $"No worker with id {id}.", new { id });
            }

            // Removing a worker mid-job would strand it: the lease it is renewing would vanish
            // underneath it and the candidate it is about to deliver would have nowhere to land.
            var held = await db.JobLeases
                .CountAsync(lease => lease.WorkerId == id && lease.State == LeaseState.Held, cancellationToken);
            if (held > 0)
            {
                return ApiErrors.Conflict(
                    "worker.stillWorking",
                    $"{worker.Name} is still holding {held} job(s). Revoke it and let the work finish or lapse, then remove it.",
                    new { id, held });
            }

            // The lease rows point at this worker and are deliberately not cascaded, so they have
            // to go first. A forgotten worker's history has no subject left to describe.
            var leases = await db.JobLeases
                .Where(lease => lease.WorkerId == id)
                .ToListAsync(cancellationToken);
            db.JobLeases.RemoveRange(leases);
            db.Workers.Remove(worker);
            await db.SaveChangesAsync(cancellationToken);

            return Results.NoContent();
        })
        .WithName("ForgetWorker")
        .Produces(StatusCodes.Status204NoContent)
        .Produces<ApiError>(StatusCodes.Status404NotFound)
        .Produces<ApiError>(StatusCodes.Status409Conflict);
    }

    private static string RedemptionMessage(PairingRedemption redemption) => redemption switch
    {
        PairingRedemption.Expired => "That pairing code has expired. Generate a new one in Optimisarr.",
        PairingRedemption.AlreadyRedeemed => "That pairing code has already been used.",
        PairingRedemption.TooManyAttempts =>
            "That pairing code was entered incorrectly too many times and is no longer valid. Generate a new one.",
        _ => "That pairing code is not correct."
    };

    private static async Task<IResult> WorkerRowAsync(
        Worker worker,
        OptimisarrDbContext db,
        CancellationToken cancellationToken)
    {
        var active = await ActiveJobsByWorkerAsync(db, cancellationToken);
        return Results.Ok(ToDto(worker, DateTimeOffset.UtcNow, active.GetValueOrDefault(worker.Id) ?? []));
    }

    private static async Task<Dictionary<int, List<WorkerJobDto>>> ActiveJobsByWorkerAsync(
        OptimisarrDbContext db,
        CancellationToken cancellationToken)
    {
        var held = await db.JobLeases
            .AsNoTracking()
            .Where(lease => lease.State == LeaseState.Held)
            .Select(lease => new
            {
                lease.WorkerId,
                lease.JobId,
                lease.Stage,
                lease.AcquiredAt,
                RelativePath = lease.Job != null && lease.Job.MediaFile != null ? lease.Job.MediaFile.RelativePath : null,
                Progress = lease.Job != null ? lease.Job.Progress : 0,
            })
            .ToListAsync(cancellationToken);

        return held
            .GroupBy(lease => lease.WorkerId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(lease => lease.AcquiredAt)
                    .Select(lease => new WorkerJobDto(
                        lease.JobId,
                        lease.RelativePath,
                        lease.Stage?.ToString() ?? "Claimed",
                        lease.Progress))
                    .ToList());
    }

    /// <summary>
    /// Stores whichever load figures the worker actually sent. Silence leaves the previous reading
    /// alone rather than blanking it: a sidecar that could not read its counters this once has not
    /// stopped being busy, and an older one that never reports must not clear what a newer one did.
    /// Out-of-range values are dropped rather than clamped, since a figure outside 0-1 means the
    /// sender is confused and guessing on its behalf would hide that.
    /// </summary>
    internal static void RecordLoad(Worker worker, double? cpu, double? gpu)
    {
        var recorded = false;
        if (cpu is { } cpuValue && double.IsFinite(cpuValue) && cpuValue is >= 0 and <= 1)
        {
            worker.CpuBusyFraction = cpuValue;
            recorded = true;
        }
        if (gpu is { } gpuValue && double.IsFinite(gpuValue) && gpuValue is >= 0 and <= 1)
        {
            worker.GpuBusyFraction = gpuValue;
            recorded = true;
        }
        if (recorded)
        {
            worker.LoadReportedAt = DateTimeOffset.UtcNow;
        }
    }

    private static WorkerDto ToDto(Worker worker, DateTimeOffset nowUtc, IReadOnlyList<WorkerJobDto> activeJobs) => new(
        worker.Id,
        worker.Name,
        worker.OperatingSystem,
        worker.Architecture,
        worker.ProtocolVersion,
        worker.SidecarVersion,
        Split(worker.VideoEncoders),
        Split(worker.AudioEncoders),
        Split(worker.HardwareDecoders),
        worker.Vmaf.ToString(),
        worker.FreeScratchBytes,
        worker.MaxConcurrency,
        // Only while it is current. A worker that stopped reporting mid-encode would otherwise go
        // on showing the busiest number it ever sent, which is worse than showing nothing.
        WorkerLiveness.IsOnline(worker.LastSeenAt, nowUtc) ? worker.CpuBusyFraction : null,
        WorkerLiveness.IsOnline(worker.LastSeenAt, nowUtc) ? worker.GpuBusyFraction : null,
        worker.LoadReportedAt,
        worker.PairedAt,
        worker.LastSeenAt,
        worker.RevokedAt,
        // Revoked workers are never "online" whatever their last heartbeat said, so the UI cannot
        // show a green light next to a worker that can no longer authenticate.
        worker.RevokedAt is null && WorkerLiveness.IsOnline(worker.LastSeenAt, nowUtc),
        worker.DrainRequestedAt,
        activeJobs.Count,
        activeJobs,
        worker.LastProblem,
        worker.LastProblemAt);

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
