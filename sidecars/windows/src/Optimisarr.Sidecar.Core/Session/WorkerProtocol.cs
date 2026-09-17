namespace Optimisarr.Sidecar.Core.Session;

/// <summary>
/// The wire contract this sidecar speaks, mirroring <c>Optimisarr.Core.Workers.WorkerProtocol</c>
/// and the macOS sidecar's copy of it.
///
/// The control plane owns the contract: it picks the highest version both ends support, and a
/// sidecar that can only speak something outside its range is refused rather than assumed
/// compatible. Advertising a range rather than a single number is what lets this service keep
/// working across a server upgrade.
/// </summary>
public static class WorkerProtocol
{
    public const int Minimum = 1;
    public const int Maximum = 2;
}
