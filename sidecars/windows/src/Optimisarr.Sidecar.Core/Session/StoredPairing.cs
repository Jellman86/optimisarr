namespace Optimisarr.Sidecar.Core.Session;

/// <summary>
/// What must survive a restart: which server, and the secret that proves who this machine is.
///
/// The credential is issued exactly once and the server cannot reissue it, so it is written to
/// storage before anything else can go wrong.
/// </summary>
public sealed record StoredPairing(string ServerAddress, string Credential, int WorkerId);
