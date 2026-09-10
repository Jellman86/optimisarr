using Optimisarr.Core.Verification;

namespace Optimisarr.Api.Workers;

/// <summary>
/// Everything a remote worker needs to execute one job, resolved by the control plane for that
/// worker's proved encoder. The argument array is the same one the local dispatcher would run,
/// built by the same code, with the worker's paths standing in as placeholders — one tested source
/// of truth for the encode contract rather than a second implementation on the other side.
/// </summary>
public sealed record RemoteAssignment(
    string VideoEncoder,
    IReadOnlyList<string> Arguments,
    string OutputExtension,
    VerificationPolicy Verification,
    string VmafModel);

/// <summary>
/// Whether a job may be offered to a worker, and why not when it may not. A refusal is the
/// ordinary answer for many jobs and workers — it names its reason so an idle sidecar can be
/// explained, and it is never an error.
/// </summary>
public sealed record RemoteWorkPlan(RemoteAssignment? Assignment, string? Reason)
{
    public bool Accepted => Assignment is not null;

    public static RemoteWorkPlan For(RemoteAssignment assignment) => new(assignment, null);

    public static RemoteWorkPlan Refused(string reason) => new(null, reason);
}
