using Optimisarr.Core.Workers;

namespace Optimisarr.Data;

/// <summary>
/// A paired remote transcoding sidecar. Optimisarr remains the control plane and the only
/// authority over destructive transitions; a worker contributes spare CPU/GPU capacity and can
/// never replace, quarantine, move, or delete an original.
///
/// The credential is stored write-only as a fingerprint, exactly like <see cref="ArrConnection"/>'s
/// API key is never returned. Revoking clears the fingerprint, which ends the worker's access
/// outright because an absent fingerprint matches nothing.
/// </summary>
public sealed class Worker
{
    public int Id { get; set; }

    /// <summary>
    /// The name the sidecar reported at pairing. Operator-facing display text supplied by the
    /// remote machine, so it is shown escaped and never used as an identifier.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Reported operating system, e.g. <c>windows</c>, <c>macos</c>, <c>linux</c>.</summary>
    public string OperatingSystem { get; set; } = string.Empty;

    /// <summary>Reported CPU architecture, e.g. <c>x64</c>, <c>arm64</c>.</summary>
    public string Architecture { get; set; } = string.Empty;

    /// <summary>The protocol version agreed at pairing, from <see cref="WorkerProtocol.Negotiate"/>.</summary>
    public int ProtocolVersion { get; set; }

    /// <summary>Comma-separated encoders the worker proved, not assumed from its platform.</summary>
    public string VideoEncoders { get; set; } = string.Empty;

    /// <summary>Comma-separated hardware decoders the worker proved.</summary>
    public string HardwareDecoders { get; set; } = string.Empty;

    public VmafCapability Vmaf { get; set; } = VmafCapability.None;

    /// <summary>Free scratch space last reported. Zero until the worker reports.</summary>
    public long FreeScratchBytes { get; set; }

    /// <summary>Jobs the worker will accept at once. Zero means drained — no new assignments.</summary>
    public int MaxConcurrency { get; set; }

    /// <summary>
    /// SHA-256 fingerprint of the issued credential. Never the credential itself, and never
    /// returned to any client. Cleared on revocation.
    /// </summary>
    public string? CredentialFingerprint { get; set; }

    public DateTimeOffset PairedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>Set when an operator revokes the worker. A revoked row is kept for the audit trail.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
