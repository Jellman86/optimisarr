namespace Optimisarr.Core.Workers;

/// <summary>
/// The outcome of comparing a sidecar's supported protocol range against this control plane's.
/// <see cref="AgreedVersion"/> is zero whenever <see cref="Compatible"/> is false, so a caller
/// cannot mistake a refusal for a usable version.
/// </summary>
public sealed record ProtocolNegotiation(bool Compatible, int AgreedVersion, string? Reason)
{
    public static ProtocolNegotiation Agreed(int version) => new(true, version, null);

    public static ProtocolNegotiation Refused(string reason) => new(false, 0, reason);
}

/// <summary>
/// The versioned wire contract between the control plane and a remote transcoding sidecar.
/// The main app owns the contract: a worker built against a newer protocol falls back to what
/// this build speaks, never the other way round.
/// </summary>
public static class WorkerProtocol
{
    /// <summary>The newest contract version this build speaks.</summary>
    public const int Current = 1;

    /// <summary>The oldest contract version this build still accepts.</summary>
    public const int MinimumSupported = 1;

    /// <summary>
    /// Picks the highest version both sides support, or refuses with a reason. Refusal is the
    /// safe outcome: assuming an unknown sidecar is compatible is exactly the silent-upgrade
    /// failure this negotiation exists to prevent.
    /// </summary>
    public static ProtocolNegotiation Negotiate(int workerMinimum, int workerMaximum)
    {
        if (workerMinimum > workerMaximum)
        {
            return ProtocolNegotiation.Refused(
                $"The worker reported an inverted protocol range ({workerMinimum}–{workerMaximum}).");
        }

        if (workerMaximum < MinimumSupported)
        {
            return ProtocolNegotiation.Refused(
                $"The worker speaks an older protocol than this build supports " +
                $"(worker up to {workerMaximum}, minimum supported {MinimumSupported}). Upgrade the worker.");
        }

        if (workerMinimum > Current)
        {
            return ProtocolNegotiation.Refused(
                $"The worker requires a newer protocol than this build speaks " +
                $"(worker from {workerMinimum}, current {Current}). Upgrade Optimisarr.");
        }

        return ProtocolNegotiation.Agreed(Math.Min(workerMaximum, Current));
    }
}
