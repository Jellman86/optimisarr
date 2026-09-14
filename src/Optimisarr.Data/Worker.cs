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

    /// <summary>
    /// The sidecar's own build, as its author versions it — not the protocol version above, which
    /// says only what the two ends agreed to speak. Without this an operator cannot tell whether a
    /// worker is running the build they think they installed, and a fix shipped to the server can
    /// sit unused on the machine that needs it with nothing on screen to say so.
    ///
    /// Free text from the remote machine: shown escaped, never parsed, never compared to decide
    /// what a worker may be offered. Empty for a worker paired before the server began asking, and
    /// for any sidecar that does not report one.
    /// </summary>
    public string SidecarVersion { get; set; } = string.Empty;

    /// <summary>Comma-separated encoders the worker proved, not assumed from its platform.</summary>
    public string VideoEncoders { get; set; } = string.Empty;

    /// <summary>Comma-separated hardware decoders the worker proved.</summary>
    /// <summary>
    /// Audio encoders this worker proved, comma separated. Empty for a worker paired before the
    /// server began asking, which is read as "none proved" and keeps such a worker off any job
    /// that re-encodes audio until its next check-in refreshes this.
    /// </summary>
    public string AudioEncoders { get; set; } = string.Empty;

    public string HardwareDecoders { get; set; } = string.Empty;

    public VmafCapability Vmaf { get; set; } = VmafCapability.None;

    /// <summary>Free scratch space last reported. Zero until the worker reports.</summary>
    public long FreeScratchBytes { get; set; }

    /// <summary>
    /// How busy the worker's machine said it was, 0 to 1, when it last reported. Null means it has
    /// not said — an older sidecar, a machine whose counters could not be read, or a reading with
    /// nothing to compare against. Deliberately nullable rather than defaulting to zero, because
    /// "idle" and "no answer" are different things to show someone deciding where work should go.
    /// </summary>
    public double? CpuBusyFraction { get; set; }

    /// <summary>
    /// The worker's accelerator utilisation, 0 to 1, or null if it has not said.
    ///
    /// Low does not mean unused. On Apple silicon a VideoToolbox encode runs on a dedicated media
    /// engine that is not the GPU's shader cores and is not reported here at all, so a Mac can be
    /// flat out encoding while this reads near zero. Shown next to the encoder in use, never alone.
    /// </summary>
    public double? GpuBusyFraction { get; set; }

    /// <summary>
    /// When the two figures above were reported. A load reading is only worth showing while it is
    /// recent: without this, a worker that went offline mid-encode would go on displaying the
    /// busiest number it ever sent.
    /// </summary>
    public DateTimeOffset? LoadReportedAt { get; set; }

    /// <summary>Jobs the worker will accept at once. Zero means drained — no new assignments.</summary>
    public int MaxConcurrency { get; set; }

    /// <summary>
    /// SHA-256 fingerprint of the issued credential. Never the credential itself, and never
    /// returned to any client. Cleared on revocation.
    /// </summary>
    public string? CredentialFingerprint { get; set; }

    public DateTimeOffset PairedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>
    /// Set when an operator asks the worker to stop taking new work. A draining worker keeps every
    /// lease it holds, finishes and delivers them, and is simply offered nothing more until this
    /// is cleared. Distinct from the worker reporting zero concurrency itself, which is the
    /// sidecar's own choice and changes with every heartbeat.
    /// </summary>
    public DateTimeOffset? DrainRequestedAt { get; set; }

    /// <summary>
    /// The most recent thing the server refused or discarded from this worker, in operator
    /// language: a lapsed lease, a candidate from the wrong source, a delivered candidate that
    /// failed verification. Null until something goes wrong; never cleared by success, because
    /// the point is that an operator can still read it after the fact.
    /// </summary>
    public string? LastProblem { get; set; }

    public DateTimeOffset? LastProblemAt { get; set; }

    /// <summary>Set when an operator revokes the worker. A revoked row is kept for the audit trail.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
