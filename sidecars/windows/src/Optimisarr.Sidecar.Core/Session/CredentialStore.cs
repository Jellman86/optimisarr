using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>Where a paired credential is kept between restarts.</summary>
public interface ICredentialStore
{
    StoredPairing? Load();
    void Save(StoredPairing pairing);
    void Clear();
}

/// <summary>
/// DPAPI-protected storage under ProgramData.
///
/// <para><b>Machine scope, not user scope.</b> This runs as a Windows service, so there is no user
/// profile to key a secret to — and the whole point of the service is that it works with nobody
/// logged in. A user-scoped secret would be unreadable the moment it mattered.</para>
///
/// <para><b>Nothing blocks startup.</b> The macOS sidecar shipped a build that hung on launch
/// waiting for an unanswerable Keychain prompt, with no window and no log line to show for it. A
/// credential that cannot be read here is treated as "not paired" and reported, never waited on:
/// an unreadable secret is unrecoverable either way, and pairing again is seconds of work.</para>
///
/// <para>The file's ACL is left to the directory it sits in: ProgramData subdirectories created by
/// an installer running as Administrator do not grant ordinary users write access, and the
/// contents are DPAPI-sealed to this machine regardless.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialStore : ICredentialStore
{
    // Tied to this application so another DPAPI consumer on the same machine cannot unseal it by
    // accident; it is not a secret and does not need to be.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("uk.optimisarr.sidecar/worker-credential/v1");

    private readonly string _path;

    public DpapiCredentialStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Optimisarr", "Sidecar", "pairing.dat");
    }

    public StoredPairing? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var plain = ProtectedData.Unprotect(
                File.ReadAllBytes(_path), Entropy, DataProtectionScope.LocalMachine);
            return JsonSerializer.Deserialize<StoredPairing>(plain);
        }
        catch (Exception exception) when (
            exception is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            // Sealed by a different machine, truncated, or unreadable. Take it away rather than
            // leave it to fail identically on every future start; the secret is unrecoverable
            // either way and the honest state is "not paired".
            Clear();
            return null;
        }
    }

    public void Save(StoredPairing pairing)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var sealed_ = ProtectedData.Protect(
            JsonSerializer.SerializeToUtf8Bytes(pairing), Entropy, DataProtectionScope.LocalMachine);

        // Written whole then moved into place, so an interrupted write cannot leave a half-file
        // that reads as a corrupt pairing. The credential is issued once and cannot be reissued.
        var temporary = _path + ".tmp";
        File.WriteAllBytes(temporary, sealed_);
        File.Move(temporary, _path, overwrite: true);
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: a credential that cannot be deleted is still one this build will refuse
            // to use, because Load() only returns what it could actually unseal.
        }
    }
}

/// <summary>For tests, and deliberately not used by the service.</summary>
public sealed class InMemoryCredentialStore(StoredPairing? stored = null) : ICredentialStore
{
    private readonly Lock _gate = new();
    private StoredPairing? _stored = stored;

    public StoredPairing? Load() { lock (_gate) { return _stored; } }
    public void Save(StoredPairing pairing) { lock (_gate) { _stored = pairing; } }
    public void Clear() { lock (_gate) { _stored = null; } }
}
